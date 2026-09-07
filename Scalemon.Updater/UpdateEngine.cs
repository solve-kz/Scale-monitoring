using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common.Updates;

namespace Scalemon.Updater;

/// <summary>Сохранённые предпочтения и последняя принятая версия.</summary>
public sealed record UpdaterState
{
    public string Current { get; init; } = "";
    public string? Previous { get; init; }
    public UpdateSettings Settings { get; init; } = new();
    public string[] IgnoredVersions { get; init; } = [];
    public string[] History { get; init; } = [];
}

/// <summary>Последовательный исполнитель обновлений с устойчивым журналом и локальным управлением.</summary>
public sealed class UpdateEngine(WindowsApplicationService service, SignedPackages packages, ILogger<UpdateEngine> logger) : BackgroundService
{
    private readonly object _sync = new();
    private readonly Channel<UpdateRequest> _commands = Channel.CreateBounded<UpdateRequest>(8);
    private volatile UpdaterState _saved = new();
    private volatile ReleaseManifest? _available;
    private volatile Uri? _archive;
    private volatile ReadinessSnapshot? _readiness;
    private string _state = "Starting";
    private string _message = "Запуск службы обновлений";
    private long _downloaded;
    private CancellationTokenSource? _operationCancellation;
    private volatile bool _switching;
    private volatile bool _initialized;
    private bool _stateLoaded;
    private bool _recovering;
    private string? _commandInitiator;
    private string? _observedInstance;
    private DateTimeOffset? _observedDisconnected;
    private DateTimeOffset _nextCheck;
    private DateTimeOffset _nextAttempt;
    private static string StatePath => Path.Combine(InstallationPaths.Updates, "state.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(InstallationPaths.Updates);
        if (!Directory.Exists(InstallationPaths.Versions) ||
            File.GetAttributes(InstallationPaths.Updates).HasFlag(FileAttributes.ReparsePoint) ||
            File.GetAttributes(InstallationPaths.Versions).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Каталоги установки отсутствуют или являются файловыми ссылками.");
        var server = LocalUpdatePipe.ServeAsync(LocalUpdatePipe.Updater, HandleAsync,
            ex => logger.LogWarning(ex, "Ошибка локального управления обновлением"), stoppingToken);
        try
        {
            var installation = AtomicJson.Read<InstallationRecord>(InstallationPaths.Installation)
                ?? throw new InvalidOperationException("Запустите установщик: регистрация установки отсутствует.");
            _saved = AtomicJson.Read<UpdaterState>(StatePath) ?? new() { Current = installation.CurrentVersion };
            _saved.Settings.Validate();
            _ = ReleaseVersion.Parse(_saved.Current);
            _stateLoaded = true;
            await RecoverAsync(stoppingToken);
            _initialized = true;
            SetStatus("Idle", "Готово");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_initialized)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                    if (_commands.Reader.TryRead(out var command)) await ExecuteCommandAsync(command, stoppingToken);
                    else
                    {
                        await ObserveAsync(stoppingToken);
                        if (DateTimeOffset.UtcNow >= _nextCheck)
                        {
                            _nextCheck = DateTimeOffset.UtcNow.AddHours(1);
                            await RunCancellableAsync(CheckAsync, stoppingToken);
                        }
                        if (_available is not null && !_saved.IgnoredVersions.Contains(_available.Version) && DateTimeOffset.UtcNow >= _nextAttempt)
                        {
                            if (_saved.Settings.AutoDownload && !File.Exists(PackagePath(_available)))
                                await RunCancellableAsync(DownloadAsync, stoppingToken);
                            if (_saved.Settings.AutoInstall && File.Exists(PackagePath(_available)) && CanInstall(true))
                                await RunCancellableAsync(ct => InstallAsync(false, true, ct), stoppingToken);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                { SetStatus("Cancelled", "Операция отменена"); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _nextAttempt = DateTimeOffset.UtcNow.AddMinutes(15);
                    logger.LogWarning(ex, "Операция обновления отложена");
                    if (_initialized) SetStatus("Deferred", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Автообновления остановлены до восстановления установки");
            SetStatus("Failed", ex.Message);
            // Pipe остаётся доступным для диагностики, но не разрешает новые операции.
            try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        }
        await server;
    }

    private Task<UpdateReply> HandleAsync(UpdateRequest request, CancellationToken ct)
    {
        lock (_sync)
        {
            if (request.Command == "status") return Task.FromResult(new UpdateReply(true, "", Snapshot()));
            if (request.Command == "recover")
            {
                if (_initialized || _recovering) return Task.FromResult(new UpdateReply(false, "Аварийное восстановление сейчас не требуется.", Snapshot()));
                _recovering = true;
                _ = RecoverOnRequestAsync();
                return Task.FromResult(new UpdateReply(true, "Запущено восстановление. После успешного завершения перезапустите Scalemon.Updater.", Snapshot()));
            }
            if (!_initialized) return Task.FromResult(new UpdateReply(false, "Восстановление установки не завершено.", Snapshot()));
            if (request.Command == "settings")
            {
                if (_switching) throw new InvalidOperationException("Дождитесь завершения переключения версии.");
                var settings = request.Settings ?? throw new InvalidDataException("Нет настроек."); settings.Validate();
                _operationCancellation?.Cancel();
                var changedChannel = settings.Channel != _saved.Settings.Channel;
                Save(_saved with { Settings = settings });
                if (changedChannel) { _available = null; _archive = null; _nextCheck = DateTimeOffset.MinValue; }
                return Task.FromResult(new UpdateReply(true, "Настройки сохранены", Snapshot()));
            }
            if (request.Command == "cancel")
            {
                if (_switching) return Task.FromResult(new UpdateReply(false, "Файлы уже переключаются; дождитесь результата и используйте возврат версии.", Snapshot()));
                if (_available is not null) Ignore(_available.Version);
                _operationCancellation?.Cancel();
                while (_commands.Reader.TryRead(out _)) { }
                return Task.FromResult(new UpdateReply(true, "Ожидающая установка отменена", Snapshot()));
            }
            if (request.Command is not ("check" or "install" or "rollback")) throw new InvalidDataException("Неизвестная команда.");
            if (_switching) throw new InvalidOperationException("Уже выполняется переключение версии.");
            if (request.Command == "install" && (request.Version is null || request.Version != _available?.Version)) throw new InvalidDataException("Выберите доступную версию.");
            if (request.Command == "rollback" && _saved.Previous is null) throw new InvalidOperationException("Предыдущая версия отсутствует.");
            if (!_commands.Writer.TryWrite(request)) throw new InvalidOperationException("Очередь команд заполнена.");
            return Task.FromResult(new UpdateReply(true, "Команда принята", Snapshot()));
        }
    }
    private UpdateStatus Snapshot() => new(_saved.Current, _saved.Previous, _available, _state, _message,
        Interlocked.Read(ref _downloaded), _saved.Settings, _readiness, _saved.History);
    private async Task ExecuteCommandAsync(UpdateRequest command, CancellationToken ct)
    {
        _commandInitiator = command.Actor;
        try
        {
            await RunCancellableAsync(async token =>
            {
                if (command.Command == "check") { await CheckAsync(token); return; }
                if (command.Command == "install")
                {
                    if (command.Version != _available?.Version) throw new InvalidOperationException("Доступная версия изменилась.");
                    await DownloadAsync(token);
                }
                await ObserveAsync(token);
                await InstallAsync(command.Command == "rollback", false, token);
            }, ct);
        }
        finally { _commandInitiator = null; }
    }
    private async Task RunCancellableAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync) _operationCancellation = operation;
        try { await action(operation.Token); }
        finally { lock (_sync) { _operationCancellation = null; _switching = false; } }
    }
    private async Task CheckAsync(CancellationToken ct)
    {
        var channel = _saved.Settings.Channel;
        SetStatus("Checking", "Проверка GitHub Releases");
        var found = await packages.FindAsync(channel, ct);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (channel != _saved.Settings.Channel) return;
            if (found is not { } release || ReleaseVersion.Parse(release.Manifest.Version).CompareTo(ReleaseVersion.Parse(_saved.Current)) <= 0)
            { _available = null; _archive = null; SetStatus("Idle", "Новых версий нет"); return; }
            if (ReleaseVersion.Parse(release.Manifest.MinimumUpdaterVersion).CompareTo(ReleaseVersion.Parse(InstallationPaths.Version)) > 0)
                throw new InvalidOperationException("Для этого выпуска нужно обновить инфраструктуру через Setup.");
            _available = release.Manifest; _archive = release.Archive;
            SetStatus("Available", _saved.IgnoredVersions.Contains(release.Manifest.Version) ? "Версия отменена или ранее не прошла проверку" : "Доступна новая версия");
        }
    }
    private async Task DownloadAsync(CancellationToken ct)
    {
        var manifest = _available ?? throw new InvalidOperationException("Нет доступного пакета.");
        var uri = _archive ?? throw new InvalidOperationException("Нет адреса пакета.");
        CheckSpace(manifest.Size + manifest.ExpandedSize + 100L * 1024 * 1024);
        SetStatus("Downloading", "Загрузка " + manifest.Version);
        await packages.DownloadAsync(manifest, uri, bytes => Interlocked.Exchange(ref _downloaded, bytes), ct);
        SetStatus("Waiting", "Пакет загружен; ожидается безопасная остановка");
    }
    private async Task ObserveAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var reply = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("status"), timeout.Token);
            var s = reply.Readiness ?? throw new IOException("Нет телеметрии приложения.");
            if (s.Instance != _observedInstance || s.Connected || DateTimeOffset.UtcNow - s.ObservedAt >= TimeSpan.FromSeconds(15)) _observedDisconnected = null;
            _observedInstance = s.Instance;
            if (!s.Connected && DateTimeOffset.UtcNow - s.ObservedAt < TimeSpan.FromSeconds(15)) _observedDisconnected ??= DateTimeOffset.UtcNow;
            _readiness = s;
        }
        catch when (!ct.IsCancellationRequested) { _readiness = null; _observedDisconnected = null; }
    }
    private bool CanInstall(bool automatic) => _readiness is { } s && s.Version == _saved.Current && UpdatePolicy.Safe(s, DateTimeOffset.UtcNow) &&
        _observedDisconnected is { } since && DateTimeOffset.UtcNow - since >= TimeSpan.FromMinutes(10) &&
        (!automatic || UpdatePolicy.InWindow(_saved.Settings, DateTime.Now));

    private async Task InstallAsync(bool rollback, bool automatic, CancellationToken ct)
    {
        if (!CanInstall(automatic)) throw new InvalidOperationException("Ожидаются 10 минут отключения весов, сохранение записей и допустимое время установки.");
        var target = rollback ? _saved.Previous ?? throw new InvalidOperationException("Нет резервной версии.") : _available?.Version ?? throw new InvalidOperationException("Нет кандидата.");
        var targetDir = VersionDirectory(target);
        if (!rollback)
        {
            var manifest = _available!;
            if (!await SignedPackages.HashMatchesAsync(PackagePath(manifest), manifest, ct)) throw new InvalidDataException("Пакет повреждён.");
            CheckSpace(manifest.ExpandedSize + 100L * 1024 * 1024);
            if (target != _saved.Current && target != _saved.Previous) DeleteVersion(target);
            SignedPackages.Extract(PackagePath(manifest), targetDir, manifest);
            AtomicJson.Write(Path.Combine(targetDir, "installed-manifest.json"), manifest);
            VersionInventory.Create(targetDir);
        }
        VersionInventory.Verify(targetDir);
        VersionInventory.Verify(VersionDirectory(_saved.Current));
        if (!File.Exists(Path.Combine(VersionDirectory(_saved.Current), "Scalemon.ServiceHost.exe")) || !File.Exists(Path.Combine(targetDir, "Scalemon.ServiceHost.exe")))
            throw new IOException("Нет полного текущего или резервного комплекта.");
        var operation = new UpdateOperation { FromVersion = _saved.Current, ToVersion = target,
            ServiceName = service.Name, OriginalImagePath = service.ImagePath, Stage = UpdateStage.Prepared,
            Initiator = automatic ? "Automatic" : (_commandInitiator ?? "Administrator") + (rollback ? " rollback" : " install") };
        bool journaled = false;
        bool accepted = false;
        try
        {
            SetStatus("Preparing", "Завершение операций приложения");
            var prepared = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("prepare"), ct);
            if (!prepared.Success || prepared.Readiness is not { Prepared: true }) throw new InvalidOperationException(prepared.Message);
            await ObserveAsync(ct);
            if (!CanInstall(automatic) || _readiness is not { Prepared: true }) throw new InvalidOperationException("Условия установки изменились.");
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                if (automatic && (!_saved.Settings.AutoInstall || _saved.IgnoredVersions.Contains(target))) throw new OperationCanceledException();
                _switching = true;
                AtomicJson.Write(InstallationPaths.Journal, operation); journaled = true;
            }
            // После фиксации журнала отмена пользователем не прерывает переключение.
            using var finish = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            SetStatus("Installing", "Остановка службы и переключение версии");
            await service.StopAsync(finish.Token);
            operation = WriteStage(operation, UpdateStage.Stopped);
            BackupSettings(operation.Id);
            operation = WriteStage(operation, UpdateStage.Switching);
            await service.SetVersionAsync(target, finish.Token);
            operation = WriteStage(operation, UpdateStage.Validating);
            await service.StartAsync(finish.Token);
            await ValidateAsync(target, finish.Token);
            Accept(operation, target, operation.FromVersion, rollback);
            accepted = true;
            await ResumeAsync(finish.Token);
            CleanupVersions();
            SetStatus("Idle", rollback ? "Предыдущая версия восстановлена; автоустановка приостановлена" : "Обновление установлено");
        }
        catch (Exception ex)
        {
            if (accepted || AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal) is { Stage: UpdateStage.Accepted, Id: var acceptedId } && acceptedId == operation.Id)
            {
                // После принятия могла начаться работа: поздняя ошибка связи/очистки не означает откат.
                SetStatus("Accepted", "Версия принята; требуется повторное подключение или обслуживание: " + ex.Message);
                return;
            }
            if (journaled)
            {
                try
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                    await RestoreAsync(operation with { Error = ex.Message }, restore.Token);
                }
                catch (Exception restoreError)
                {
                    WriteStage(operation with { Error = restoreError.Message }, UpdateStage.Failed);
                    _initialized = false;
                    SetStatus("Failed", "Возврат не завершён. Используйте локальное обслуживание: " + restoreError.Message);
                    throw;
                }
            }
            else
            {
                try { using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await ResumeAsync(cancel.Token); } catch { }
            }
            throw;
        }
        finally { lock (_sync) _switching = false; }
    }
    private async Task ValidateAsync(string version, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                var reply = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("status"), timeout.Token);
                if (reply.Readiness is { Healthy: true, Maintenance: true } s && s.Version == version) return;
            }
            catch (IOException) { } catch (TimeoutException) { }
            await Task.Delay(1000, timeout.Token);
        }
    }
    private async Task RecoverAsync(CancellationToken ct)
    {
        var operation = AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal);
        if (operation is null || operation.Stage is UpdateStage.Idle) return;
        if (operation.Stage == UpdateStage.Accepted)
        {
            // Принятая версия не откатывается после начала новых взвешиваний.
            if (_saved.Current != operation.ToVersion) Save(_saved with { Current = operation.ToVersion,
                Previous = operation.FromVersion == operation.ToVersion ? _saved.Previous : operation.FromVersion });
            try { await ResumeAsync(ct); } catch (IOException) { } catch (TimeoutException) { }
            return;
        }
        if (operation.Stage == UpdateStage.Failed) throw new InvalidOperationException("Предыдущая операция требует локального восстановления. " + operation.Error);
        if (operation.Stage == UpdateStage.Prepared)
        {
            // До переключения регистрации старый процесс мог продолжить работу по истечении lease.
            // Не останавливаем его повторно: отменяем незавершённую подготовку.
            AtomicJson.Write(InstallationPaths.Journal, operation with { ToVersion = operation.FromVersion, Stage = UpdateStage.Accepted });
            await service.StartAsync(ct);
            await ResumeWhenAvailableAsync(ct);
            Save(_saved with { Settings = _saved.Settings with { AutoInstall = false } });
            return;
        }
        SetStatus("Recovering", "Восстановление незавершённого обновления");
        if (!UpdateRecoveryPolicy.MayRestore(operation.Stage)) throw new InvalidDataException("Неизвестное состояние восстановления.");
        await RestoreAsync(operation, ct);
    }
    private async Task RecoverOnRequestAsync()
    {
        try
        {
            var operation = AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal)
                ?? throw new InvalidOperationException("Нет журнала для восстановления. Требуется Setup.");
            if (operation.Stage is UpdateStage.Accepted or UpdateStage.Idle) throw new InvalidOperationException("Операция уже завершена. Требуется диагностика установки.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            await RestoreAsync(operation, timeout.Token);
        }
        catch (Exception ex) { SetStatus("Failed", "Восстановление не завершено: " + ex.Message); }
        finally { lock (_sync) _recovering = false; }
    }
    private async Task RestoreAsync(UpdateOperation operation, CancellationToken ct)
    {
        VersionInventory.Verify(VersionDirectory(operation.FromVersion));
        operation = WriteStage(operation, UpdateStage.RollingBack);
        await service.StopAsync(ct);
        await service.RestorePathAsync(operation.OriginalImagePath, ct);
        await service.StartAsync(ct);
        await ValidateAsync(operation.FromVersion, ct);
        lock (_sync)
        {
            Ignore(operation.ToVersion);
            Save(_saved with { Current = operation.FromVersion, Settings = _saved.Settings with { AutoInstall = false } });
            AtomicJson.Write(InstallationPaths.Journal, operation with { ToVersion = operation.FromVersion, Stage = UpdateStage.Accepted });
        }
        await ResumeAsync(ct);
        SetStatus("RolledBack", "Возвращена версия " + operation.FromVersion + ". Автоустановка приостановлена.");
    }
    private void Accept(UpdateOperation operation, string current, string previous, bool rollback)
    {
        lock (_sync)
        {
            // Accepted фиксируется до снятия барьера: после этого откат данных запрещён.
            AtomicJson.Write(InstallationPaths.Journal, operation with { Stage = UpdateStage.Accepted });
            Save(_saved with { Current = current, Previous = previous,
                Settings = rollback ? _saved.Settings with { AutoInstall = false } : _saved.Settings });
            _available = null; _archive = null; Interlocked.Exchange(ref _downloaded, 0);
        }
    }
    private static async Task ResumeAsync(CancellationToken ct)
    {
        var reply = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("resume"), ct);
        if (!reply.Success) throw new IOException(reply.Message);
    }
    private static async Task ResumeWhenAvailableAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            try { await ResumeAsync(timeout.Token); return; }
            catch (IOException) { }
            catch (TimeoutException) { }
            await Task.Delay(1000, timeout.Token);
        }
    }
    private static UpdateOperation WriteStage(UpdateOperation op, UpdateStage stage)
    { var next = op with { Stage = stage }; AtomicJson.Write(InstallationPaths.Journal, next); return next; }
    private void Save(UpdaterState value) { AtomicJson.Write(StatePath, value); _saved = value; }
    private void Ignore(string version) => Save(_saved with { IgnoredVersions = _saved.IgnoredVersions.Append(version).Distinct().ToArray() });
    private void SetStatus(string state, string message)
    {
        lock (_sync)
        {
            if (_state == state && _message == message) return;
            _state = state; _message = message;
            var entry = $"{DateTimeOffset.Now:O} [{state}] {message}";
            var next = _saved with { History = _saved.History.Append(entry).TakeLast(100).ToArray() };
            if (_stateLoaded) Save(next); else _saved = next;
            logger.LogInformation("Обновление {State}: {Message}", state, message);
        }
    }
    private static string PackagePath(ReleaseManifest manifest) => Path.Combine(InstallationPaths.Updates, manifest.Archive);
    private static string VersionDirectory(string version) { _ = ReleaseVersion.Parse(version); return Path.Combine(InstallationPaths.Versions, version); }
    private static void DeleteVersion(string version)
    {
        var directory = Path.GetFullPath(VersionDirectory(version));
        var root = Path.GetFullPath(InstallationPaths.Versions) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Неверный каталог версии.");
        if (!Directory.Exists(directory)) return;
        EnsureNoReparse(directory);
        Directory.Delete(directory, true);
    }
    private static void EnsureNoReparse(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Каталог версии содержит ссылку.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Каталог версии содержит ссылку.");
            if (attributes.HasFlag(FileAttributes.Directory)) EnsureNoReparse(entry);
        }
    }
    private void CleanupVersions()
    {
        foreach (var directory in Directory.EnumerateDirectories(InstallationPaths.Versions))
        {
            var version = Path.GetFileName(directory);
            if (version == _saved.Current || version == _saved.Previous) continue;
            try { _ = ReleaseVersion.Parse(version); DeleteVersion(version); } catch (InvalidDataException) { }
        }
    }
    private static void CheckSpace(long required)
    {
        foreach (var path in new[] { InstallationPaths.Updates, InstallationPaths.Versions })
            if (new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < required) throw new IOException("Недостаточно места для безопасного обновления.");
    }
    private static void BackupSettings(string id)
    {
        var target = Path.Combine(InstallationPaths.Root, "Backups", id); Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(InstallationPaths.Config)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
    }
}
