using Scalemon.Common;
using Scalemon.Common.Updates;
using Scalemon.WebApp.Data;

namespace Scalemon.ServiceHost.Services;

/// <summary>Согласует остановку писателей и подтверждает состояние работающей службы.</summary>
public sealed class ApplicationMaintenance : BackgroundService, IMaintenanceCoordinator
{
    private readonly IDataWriteDrain _drain;
    private readonly IWeighingModeStore _modes;
    private readonly IWeightRegisterReviewService _review;
    private readonly ILogger<ApplicationMaintenance> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SemaphoreSlim _prepareGate = new(1, 1);
    private readonly object _sync = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private DateTimeOffset _observed;
    private DateTimeOffset? _disconnected;
    private bool _connected;
    private bool _prepared;
    private bool _healthy;
    private bool _startupHold;
    private DateTimeOffset? _preparedAt;
    private CancellationTokenSource? _prepareCancellation;
    public ApplicationMaintenance(IDataWriteDrain drain, IWeighingModeStore modes,
        IWeightRegisterReviewService review, ILogger<ApplicationMaintenance> logger, IHostApplicationLifetime lifetime)
    {
        _drain = drain; _modes = modes; _review = review; _logger = logger; _lifetime = lifetime;
        // Закрытый режим при повреждении журнала важнее автоматического возобновления.
        try { _startupHold = AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal) is { Stage: not (UpdateStage.Idle or UpdateStage.Accepted) }; }
        catch { _startupHold = true; }
        if (_startupHold) MaintenanceGate.Shared.Close();
    }
    /// <summary>Принимает свежую телеметрию, в том числе во время подготовки.</summary>
    public void Observe(ScaleDataPoint data)
    {
        lock (_sync)
        {
            _observed = DateTimeOffset.UtcNow;
            _connected = data.IsConnected;
            _disconnected = _connected ? null : _disconnected ?? _observed;
            if (_connected)
            {
                try { _prepareCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            }
            if (_connected && !_startupHold && _prepared)
            { _prepared = false; _preparedAt = null; MaintenanceGate.Shared.Open(); }
        }
    }
    /// <inheritdoc />
    public ReadinessSnapshot Snapshot()
    {
        lock (_sync)
            return new(InstallationPaths.Version, _instance, _observed, _connected, _disconnected,
                MaintenanceGate.Shared.Active, _drain.PendingWrites, _drain.DatabaseAvailable,
                MaintenanceGate.Shared.IsClosed, _prepared, _healthy && _lifetime.ApplicationStarted.IsCancellationRequested,
                !_healthy ? "Проверка локальных хранилищ не завершена" : _startupHold ? "Проверка версии после переключения" :
                _connected ? "Весы подключены" : _drain.PendingWrites != 0 ? "Ожидается сохранение записей" : "Ожидание расписания и отключения весов");
    }
    /// <inheritdoc />
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _prepareGate.WaitAsync(cancellationToken);
        try
        {
            if (!UpdatePolicy.Safe(Snapshot(), DateTimeOffset.UtcNow)) throw new InvalidOperationException("Служба не готова к обновлению.");
            MaintenanceGate.Shared.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_sync) _prepareCancellation = timeout;
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            await MaintenanceGate.Shared.WaitAsync(timeout.Token);
            await _drain.DrainAsync(timeout.Token);
            lock (_sync)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!UpdatePolicy.Safe(Snapshot(), DateTimeOffset.UtcNow)) throw new InvalidOperationException("Состояние весов изменилось.");
                _prepared = true; _preparedAt = DateTimeOffset.UtcNow;
            }
        }
        catch { if (!_startupHold) Resume(); throw; }
        finally { lock (_sync) _prepareCancellation = null; _prepareGate.Release(); }
    }
    /// <inheritdoc />
    public void Resume()
    {
        lock (_sync)
        {
            try { _prepareCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            if (_startupHold)
            {
                var operation = AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal);
                if (operation is not { Stage: UpdateStage.Accepted }) throw new InvalidOperationException("Updater ещё не подтвердил безопасный запуск.");
            }
            _startupHold = false; _prepared = false; _preparedAt = null; MaintenanceGate.Shared.Open();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Проверка локальных ресурсов не зависит от выключенных весов и внешнего SQL.
        try
        {
            await _modes.EnsureInitializedAsync(stoppingToken);
            await _review.ListProjectsAsync(stoppingToken);
            Directory.CreateDirectory(InstallationPaths.Config);
            lock (_sync) _healthy = true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Локальные хранилища не готовы к обновлению"); }
        var server = File.Exists(InstallationPaths.Installation) ? LocalUpdatePipe.ServeAsync(LocalUpdatePipe.Application, HandleAsync,
            ex => _logger.LogWarning(ex, "Ошибка локального обслуживания"), stoppingToken) : Task.CompletedTask;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                lock (_sync)
                {
                    // Подготовка не оставляет службу навсегда закрытой, если Updater исчез до остановки.
                    if (!_startupHold && _preparedAt is { } time && DateTimeOffset.UtcNow - time > TimeSpan.FromMinutes(3)) Resume();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await server;
    }
    private async Task<UpdateReply> HandleAsync(UpdateRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            case "status": return new(true, "", Readiness: Snapshot());
            case "prepare": await PrepareAsync(ct); return new(true, "Подготовлено", Readiness: Snapshot());
            case "dependencies":
                if (!_healthy || !_lifetime.ApplicationStarted.IsCancellationRequested) return new(false, "Запуск ещё не завершён");
                if (!MaintenanceGate.Shared.IsClosed) return new(false, "Проверка установки требует режима обслуживания");
                await _drain.DrainAsync(ct);
                return new(true, "SQL и локальные хранилища доступны", Readiness: Snapshot());
            case "resume": Resume(); return new(true, "Рабочий режим", Readiness: Snapshot());
            default: throw new InvalidOperationException("Неизвестная команда приложения.");
        }
    }
}
