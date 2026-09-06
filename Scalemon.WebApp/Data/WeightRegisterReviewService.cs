using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Scalemon.WebApp.Models;

namespace Scalemon.WebApp.Data;

/// <summary>Управляет загрузкой, калибровкой, распознаванием и коррекциями ручных реестров.</summary>
public interface IWeightRegisterReviewService
{
    /// <summary>Возвращает сохранённые проекты, начиная с самых новых.</summary>
    Task<IReadOnlyList<WeightRegisterProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Загружает проект по безопасному идентификатору.</summary>
    Task<WeightRegisterProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Создаёт проект из набора изображений одной даты разделки.</summary>
    Task<WeightRegisterProject> CreateProjectAsync(
        DateOnly cuttingDate,
        IReadOnlyList<RegisterUploadFile> files,
        CancellationToken cancellationToken = default);

    /// <summary>Добавляет новые листы в проект текущего дня, не затрагивая уже распознанные листы.</summary>
    Task<WeightRegisterProject> AddSheetsAsync(
        string projectId,
        IReadOnlyList<RegisterUploadFile> files,
        CancellationToken cancellationToken = default);

    /// <summary>Сохраняет порядок листов, используемый при сравнении с автоматическими записями.</summary>
    Task ReorderSheetsAsync(
        string projectId,
        IReadOnlyList<string> sheetIds,
        CancellationToken cancellationToken = default);

    /// <summary>Сохраняет калибровку листа в координатах исходного изображения.</summary>
    Task SaveCalibrationAsync(
        string projectId,
        string sheetId,
        RegisterTableCalibration calibration,
        CancellationToken cancellationToken = default);

    /// <summary>Создаёт файловое задание и ставит проект в очередь распознавания.</summary>
    Task QueueRecognitionAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Атомарно забирает следующий проект из очереди фонового распознавания.</summary>
    Task<WeightRegisterProject?> TryClaimRecognitionAsync(CancellationToken cancellationToken = default);

    /// <summary>Возвращает положение проекта в очереди и состояние фонового исполнителя.</summary>
    Task<WeightRegisterRecognitionQueueInfo?> GetRecognitionQueueInfoAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Сохраняет прогресс обработки листов текущего проекта.</summary>
    Task UpdateRecognitionProgressAsync(
        string projectId,
        int completedSheets,
        int totalSheets,
        string? currentSheet,
        CancellationToken cancellationToken = default);

    /// <summary>Отмечает активность фонового исполнителя распознавания.</summary>
    void ReportRecognitionWorkerHeartbeat(string message);

    /// <summary>Отменяет ожидающее задание либо запрашивает отмену выполняющегося.</summary>
    Task CancelRecognitionAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Фиксирует завершение отмены выполнявшегося задания.</summary>
    Task FinishRecognitionCancellationAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Перемещает ожидающее задание на одну позицию вверх или вниз.</summary>
    Task MoveRecognitionAsync(
        string projectId,
        int direction,
        CancellationToken cancellationToken = default);

    /// <summary>Сохраняет безопасное описание ошибки фонового распознавания.</summary>
    Task FailRecognitionAsync(
        string projectId,
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>Импортирует современный или совместимый с WeightRegisterReviewApp JSON.</summary>
    Task ImportRecognitionAsync(
        string projectId,
        Stream json,
        CancellationToken cancellationToken = default);

    /// <summary>Завершает автоматическое распознавание результатом фонового исполнителя.</summary>
    Task<bool> CompleteRecognitionAsync(
        string projectId,
        WeightRegisterProject recognized,
        CancellationToken cancellationToken = default);

    /// <summary>Записывает исправленное значение и добавляет запись аудита.</summary>
    Task CorrectCellAsync(
        string projectId,
        string sheetId,
        int row,
        int column,
        decimal? correctedValue,
        string? editedBy,
        CancellationToken cancellationToken = default);

    /// <summary>Записывает исправленное значение итоговой ячейки и добавляет запись аудита.</summary>
    Task CorrectTotalAsync(
        string projectId,
        string sheetId,
        int totalRow,
        int column,
        decimal? correctedValue,
        string? editedBy,
        CancellationToken cancellationToken = default);

    /// <summary>Открывает исходное изображение листа для безопасной выдачи контроллером.</summary>
    Task<RegisterImageFile?> OpenImageAsync(
        string projectId,
        string sheetId,
        CancellationToken cancellationToken = default);

    /// <summary>Возвращает путь к сформированному заданию распознавания.</summary>
    string GetRecognitionRequestPath(string projectId);
}

/// <summary>Связывает отмену из веб-интерфейса с выполняющимся фоновым запросом.</summary>
public sealed class WeightRegisterRecognitionCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Регистрирует токен текущего задания.</summary>
    public CancellationTokenSource Register(string projectId, CancellationToken hostToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        if (!_jobs.TryAdd(projectId, source))
        {
            source.Dispose();
            throw new InvalidOperationException("Задание уже выполняется.");
        }
        return source;
    }

    /// <summary>Отменяет зарегистрированное выполняющееся задание.</summary>
    public bool Cancel(string projectId)
        => _jobs.TryGetValue(projectId, out var source) && TryCancel(source);

    /// <summary>Удаляет регистрацию после завершения задания.</summary>
    public void Unregister(string projectId, CancellationTokenSource source)
    {
        if (_jobs.TryGetValue(projectId, out var registered)
            && ReferenceEquals(registered, source))
        {
            _jobs.TryRemove(projectId, out _);
        }
        source.Dispose();
    }

    private static bool TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>Файловая реализация, совместимая с JSON WeightRegisterReviewApp.</summary>
public sealed class JsonWeightRegisterReviewService : IWeightRegisterReviewService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly string _storageRoot;
    private readonly long _maxUploadBytes;
    private readonly bool _automaticRecognitionEnabled;
    private readonly WeightRegisterRecognitionCancellationRegistry _cancellationRegistry;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _workerStateGate = new();
    private DateTimeOffset? _workerLastSeenAt;
    private string? _workerMessage;

    static JsonWeightRegisterReviewService()
    {
        JsonOptions.Converters.Add(new FlexibleDateOnlyJsonConverter());
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public JsonWeightRegisterReviewService(
        IOptions<WeightRegisterReviewOptions> options,
        WeightRegisterRecognitionCancellationRegistry cancellationRegistry)
    {
        _storageRoot = Path.GetFullPath(options.Value.StoragePath);
        _maxUploadBytes = Math.Max(1, options.Value.MaxUploadBytes);
        _automaticRecognitionEnabled = options.Value.Recognition.Enabled;
        _cancellationRegistry = cancellationRegistry;
        Directory.CreateDirectory(_storageRoot);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WeightRegisterProjectSummary>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<WeightRegisterProjectSummary>();
        foreach (var directory in Directory.EnumerateDirectories(_storageRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!IsProjectId(id))
            {
                continue;
            }

            try
            {
                var project = await LoadProjectCoreAsync(id, cancellationToken);
                if (project is not null)
                {
                    result.Add(new WeightRegisterProjectSummary(
                        project.Id,
                        project.CuttingDate,
                        project.ProjectName,
                        project.CreatedAt,
                        project.RecognitionStatus,
                        project.Sheets.Count));
                }
            }
            catch (JsonException)
            {
                // Повреждённый проект не мешает открыть остальные; файл остаётся для ручного восстановления.
            }
        }

        return result
            .OrderByDescending(item => item.CuttingDate)
            .ThenByDescending(item => item.CreatedAt)
            .ToArray();
    }

    /// <inheritdoc />
    public Task<WeightRegisterProject?> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
        => LoadProjectCoreAsync(projectId, cancellationToken);

    /// <inheritdoc />
    public async Task<WeightRegisterProject> CreateProjectAsync(
        DateOnly cuttingDate,
        IReadOnlyList<RegisterUploadFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Не выбрано ни одного изображения.");
        }

        var totalBytes = files.Sum(file => file.Length);
        if (totalBytes > _maxUploadBytes)
        {
            throw new InvalidOperationException($"Общий размер сканов превышает {_maxUploadBytes / 1024 / 1024} МБ.");
        }

        var project = new WeightRegisterProject
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectName = $"weight_register_scans_{cuttingDate:yyyy-MM-dd}",
            CuttingDate = cuttingDate,
            RecognitionStatus = RegisterRecognitionStatus.CalibrationRequired,
            StatusMessage = "Проверьте границы таблицы на каждом листе."
        };

        var projectDirectory = GetProjectDirectory(project.Id);
        var sourceDirectory = Path.Combine(projectDirectory, "source");
        Directory.CreateDirectory(sourceDirectory);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file.FileName);
                if (!SupportedExtensions.Contains(extension))
                {
                    throw new InvalidOperationException($"Формат {extension} не поддерживается. Загрузите JPG или PNG.");
                }

                if (file.Length <= 0)
                {
                    throw new InvalidOperationException($"Файл {file.FileName} пуст.");
                }

                var sheetId = Guid.NewGuid().ToString("N");
                var safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(file.FileName));
                var storedName = $"{sheetId}_{safeName}{extension.ToLowerInvariant()}";
                var storedPath = Path.Combine(sourceDirectory, storedName);
                await using (var destination = new FileStream(
                    storedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous))
                {
                    await file.Content.CopyToAsync(destination, cancellationToken);
                }

                var (width, height) = ReadImageDimensions(storedPath);
                project.Sheets.Add(new WeightRegisterSheet
                {
                    Id = sheetId,
                    Name = string.IsNullOrWhiteSpace(safeName) ? $"Лист {project.Sheets.Count + 1}" : safeName,
                    SourceFile = Path.Combine("source", storedName).Replace('\\', '/'),
                    CuttingDate = cuttingDate,
                    Calibration = WeightRegisterGeometry.CreateDefaultCalibration(width, height)
                });
            }

            await SaveProjectCoreAsync(project, cancellationToken);
        }
        catch
        {
            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    Directory.Delete(projectDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Исходная ошибка загрузки важнее неудачной уборки сиротской папки.
            }
            catch (UnauthorizedAccessException)
            {
                // Исходная ошибка загрузки важнее неудачной уборки сиротской папки.
            }
            throw;
        }
        finally
        {
            _writeGate.Release();
        }

        return project;
    }

    /// <inheritdoc />
    public async Task<WeightRegisterProject> AddSheetsAsync(
        string projectId,
        IReadOnlyList<RegisterUploadFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Не выбрано ни одного изображения.");
        }
        if (files.Sum(file => file.Length) > _maxUploadBytes)
        {
            throw new InvalidOperationException($"Общий размер сканов превышает {_maxUploadBytes / 1024 / 1024} МБ.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        var createdFiles = new List<string>();
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            if (project.CuttingDate != DateOnly.FromDateTime(DateTime.Today))
            {
                throw new InvalidOperationException("Добавлять листы можно только в проект текущего дня.");
            }
            if (project.RecognitionStatus is RegisterRecognitionStatus.Pending or RegisterRecognitionStatus.Processing)
            {
                throw new InvalidOperationException("Дождитесь завершения или отмените текущее распознавание перед добавлением листов.");
            }

            var sourceDirectory = Path.Combine(GetProjectDirectory(project.Id), "source");
            Directory.CreateDirectory(sourceDirectory);
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file.FileName);
                if (!SupportedExtensions.Contains(extension))
                {
                    throw new InvalidOperationException($"Формат {extension} не поддерживается. Загрузите JPG или PNG.");
                }
                if (file.Length <= 0)
                {
                    throw new InvalidOperationException($"Файл {file.FileName} пуст.");
                }

                var sheetId = Guid.NewGuid().ToString("N");
                var safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(file.FileName));
                var storedName = $"{sheetId}_{safeName}{extension.ToLowerInvariant()}";
                var storedPath = Path.Combine(sourceDirectory, storedName);
                await using (var destination = new FileStream(
                    storedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous))
                {
                    await file.Content.CopyToAsync(destination, cancellationToken);
                }
                createdFiles.Add(storedPath);

                var (width, height) = ReadImageDimensions(storedPath);
                project.Sheets.Add(new WeightRegisterSheet
                {
                    Id = sheetId,
                    Name = string.IsNullOrWhiteSpace(safeName) ? $"Лист {project.Sheets.Count + 1}" : safeName,
                    SourceFile = Path.Combine("source", storedName).Replace('\\', '/'),
                    CuttingDate = project.CuttingDate,
                    Calibration = WeightRegisterGeometry.CreateDefaultCalibration(width, height)
                });
            }

            project.RecognitionStatus = RegisterRecognitionStatus.CalibrationRequired;
            project.StatusMessage = "Добавлены новые листы. Проверьте и сохраните их калибровку.";
            project.RecognitionFinishedAt = null;
            await SaveProjectCoreAsync(project, cancellationToken);
            return project;
        }
        catch
        {
            foreach (var path in createdFiles)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReorderSheetsAsync(
        string projectId,
        IReadOnlyList<string> sheetIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheetIds);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            if (project.RecognitionStatus is RegisterRecognitionStatus.Pending or RegisterRecognitionStatus.Processing)
            {
                throw new InvalidOperationException("Нельзя менять порядок листов во время распознавания.");
            }

            if (sheetIds.Count != project.Sheets.Count
                || sheetIds.Distinct(StringComparer.Ordinal).Count() != project.Sheets.Count)
            {
                throw new InvalidOperationException("Передан неполный или повторяющийся список листов.");
            }

            var sheetsById = project.Sheets.ToDictionary(sheet => sheet.Id, StringComparer.Ordinal);
            if (sheetIds.Any(sheetId => !sheetsById.ContainsKey(sheetId)))
            {
                throw new InvalidOperationException("В порядке листов указан неизвестный лист.");
            }

            project.Sheets = sheetIds.Select(sheetId => sheetsById[sheetId]).ToList();
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveCalibrationAsync(
        string projectId,
        string sheetId,
        RegisterTableCalibration calibration,
        CancellationToken cancellationToken = default)
    {
        ValidateCalibration(calibration);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            var sheet = project.Sheets.SingleOrDefault(item => item.Id == sheetId)
                ?? throw new KeyNotFoundException("Лист проекта не найден.");
            sheet.Calibration = calibration.Copy();
            sheet.IsCalibrationConfirmed = true;
            var pendingSheets = project.Sheets.Where(item => !item.IsRecognitionComplete).ToArray();
            project.RecognitionStatus = pendingSheets.Length == 0
                ? RegisterRecognitionStatus.Completed
                : pendingSheets.All(item => item.IsCalibrationConfirmed)
                    ? RegisterRecognitionStatus.Ready
                    : RegisterRecognitionStatus.CalibrationRequired;
            project.StatusMessage = project.RecognitionStatus == RegisterRecognitionStatus.Ready
                ? "Калибровка сохранена. Можно запускать распознавание."
                : "Откалибруйте оставшиеся листы.";
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task QueueRecognitionAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            var pendingSheets = project.Sheets.Where(sheet => !sheet.IsRecognitionComplete).ToArray();
            if (pendingSheets.Length == 0)
            {
                throw new InvalidOperationException("Все листы проекта уже распознаны.");
            }
            if (pendingSheets.Any(sheet => !sheet.IsCalibrationConfirmed || sheet.Calibration is null))
            {
                throw new InvalidOperationException("Сначала сохраните калибровку новых листов.");
            }
            if (project.RecognitionStatus is RegisterRecognitionStatus.Pending or RegisterRecognitionStatus.Processing)
            {
                throw new InvalidOperationException("Проект уже находится в очереди распознавания.");
            }

            var request = new
            {
                schemaVersion = 1,
                projectId = project.Id,
                cuttingDate = project.CuttingDate.ToString("dd.MM.yyyy"),
                resultFile = "recognition-result.json",
                instructions = new
                {
                    skill = "weight-register-scans",
                    dataOrder = "40 rows x 10 columns",
                    preserveUncertainCells = true,
                    visualReviewRequired = true,
                    readSlaughterAndCuttingDatesFromImage = true,
                    maxUnexpectedEmptyPercent = 2,
                    maxMassSuspectWithoutReasonPercent = 10,
                    validateCells = new[] { "1:1", "1:10", "40:1", "40:10" }
                },
                sheets = pendingSheets.Select(sheet => new
                {
                    id = sheet.Id,
                    name = sheet.Name,
                    source_file = sheet.SourceFile,
                    cutting_date = project.CuttingDate.ToString("dd.MM.yyyy"),
                    table = sheet.Table,
                    calibration = sheet.Calibration,
                    data = Array.Empty<string[]>(),
                    totals = Array.Empty<string>(),
                    uncertain = Array.Empty<object>()
                })
            };
            var requestPath = GetRecognitionRequestPath(projectId);
            await using (var stream = new FileStream(
                requestPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, request, JsonOptions, cancellationToken);
            }

            var queuedAt = DateTimeOffset.Now;
            project.RecognitionStatus = RegisterRecognitionStatus.Pending;
            project.RecognitionQueueOrder = queuedAt.UtcDateTime.Ticks;
            project.RecognitionCancellationRequested = false;
            project.RecognitionQueuedAt = queuedAt;
            project.RecognitionStartedAt = null;
            project.RecognitionFinishedAt = null;
            project.RecognitionUpdatedAt = queuedAt;
            project.RecognitionCompletedSheets = 0;
            project.RecognitionTotalSheets = pendingSheets.Length;
            project.RecognitionCurrentSheet = null;
            project.StatusMessage = _automaticRecognitionEnabled
                ? "Задание поставлено в очередь автоматического распознавания."
                : "Автоматическое распознавание выключено: скачайте ZIP-пакет с заданием и сканами, затем импортируйте recognition-result.json.";
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WeightRegisterProject?> TryClaimRecognitionAsync(
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.Now;
            var candidates = new List<WeightRegisterProject>();
            foreach (var directory in Directory.EnumerateDirectories(_storageRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectId = Path.GetFileName(directory);
                if (!IsProjectId(projectId))
                {
                    continue;
                }

                try
                {
                    var project = await LoadProjectCoreAsync(projectId, cancellationToken);
                    if (project?.RecognitionStatus == RegisterRecognitionStatus.Processing
                        && project.RecognitionCancellationRequested)
                    {
                        SetRecognitionCancelled(project, DateTimeOffset.Now);
                        await SaveProjectCoreAsync(project, cancellationToken);
                        continue;
                    }
                    if (project is not null
                        && (project.RecognitionStatus == RegisterRecognitionStatus.Pending
                            || project.RecognitionStatus == RegisterRecognitionStatus.Processing))
                    {
                        candidates.Add(project);
                    }
                }
                catch (JsonException)
                {
                    // Повреждённый проект не должен останавливать очередь остальных проектов.
                }
            }

            var claimed = candidates
                .OrderBy(project => project.RecognitionStatus == RegisterRecognitionStatus.Processing ? 0 : 1)
                .ThenBy(QueueSortValue)
                .FirstOrDefault();
            if (claimed is null)
            {
                return null;
            }

            claimed.RecognitionStatus = RegisterRecognitionStatus.Processing;
            claimed.RecognitionCancellationRequested = false;
            claimed.RecognitionAttempt++;
            claimed.RecognitionStartedAt = now;
            claimed.RecognitionFinishedAt = null;
            claimed.RecognitionUpdatedAt = now;
            claimed.RecognitionCompletedSheets = 0;
            var pendingSheets = claimed.Sheets.Where(sheet => !sheet.IsRecognitionComplete).ToArray();
            claimed.RecognitionTotalSheets = pendingSheets.Length;
            claimed.RecognitionCurrentSheet = pendingSheets.FirstOrDefault()?.Name;
            claimed.StatusMessage = $"Распознавание выполняется, попытка {claimed.RecognitionAttempt}.";
            await SaveProjectCoreAsync(claimed, cancellationToken);
            return claimed;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WeightRegisterRecognitionQueueInfo?> GetRecognitionQueueInfoAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var active = new List<WeightRegisterProject>();
            foreach (var directory in Directory.EnumerateDirectories(_storageRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateId = Path.GetFileName(directory);
                if (!IsProjectId(candidateId))
                {
                    continue;
                }

                try
                {
                    var candidate = await LoadProjectCoreAsync(candidateId, cancellationToken);
                    if (candidate?.RecognitionStatus is RegisterRecognitionStatus.Pending or RegisterRecognitionStatus.Processing)
                    {
                        active.Add(candidate);
                    }
                }
                catch (JsonException)
                {
                    // Повреждённый проект не мешает показать состояние остальных заданий.
                }
            }

            var ordered = active
                .OrderBy(project => project.RecognitionStatus == RegisterRecognitionStatus.Processing ? 0 : 1)
                .ThenBy(QueueSortValue)
                .ToArray();
            var position = Array.FindIndex(ordered, project => project.Id == projectId);
            DateTimeOffset? workerLastSeenAt;
            string? workerMessage;
            lock (_workerStateGate)
            {
                workerLastSeenAt = _workerLastSeenAt;
                workerMessage = _workerMessage;
            }

            return new WeightRegisterRecognitionQueueInfo(
                ordered.Count(project => project.RecognitionStatus == RegisterRecognitionStatus.Pending),
                ordered.Count(project => project.RecognitionStatus == RegisterRecognitionStatus.Processing),
                position >= 0 ? position + 1 : null,
                ordered.Length,
                _automaticRecognitionEnabled,
                workerLastSeenAt,
                workerMessage,
                ordered.Select((project, index) => new WeightRegisterRecognitionQueueItem(
                    project.Id,
                    project.ProjectName,
                    project.CuttingDate,
                    project.RecognitionStatus,
                    index + 1,
                    project.RecognitionQueuedAt,
                    project.RecognitionCancellationRequested)).ToArray());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateRecognitionProgressAsync(
        string projectId,
        int completedSheets,
        int totalSheets,
        string? currentSheet,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            if (project.RecognitionStatus != RegisterRecognitionStatus.Processing)
            {
                return;
            }

            var safeTotal = Math.Max(0, totalSheets);
            project.RecognitionCompletedSheets = Math.Clamp(completedSheets, 0, safeTotal);
            project.RecognitionTotalSheets = safeTotal;
            project.RecognitionCurrentSheet = string.IsNullOrWhiteSpace(currentSheet) ? null : currentSheet.Trim();
            project.RecognitionUpdatedAt = DateTimeOffset.Now;
            project.StatusMessage = project.RecognitionCurrentSheet is null
                ? $"Обработано листов: {project.RecognitionCompletedSheets} из {safeTotal}."
                : $"Обрабатывается лист «{project.RecognitionCurrentSheet}»; завершено {project.RecognitionCompletedSheets} из {safeTotal}.";
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public void ReportRecognitionWorkerHeartbeat(string message)
    {
        lock (_workerStateGate)
        {
            _workerLastSeenAt = DateTimeOffset.Now;
            _workerMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
    }

    /// <inheritdoc />
    public async Task CancelRecognitionAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var cancelRunning = false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            if (project.RecognitionStatus == RegisterRecognitionStatus.Pending)
            {
                SetRecognitionCancelled(project, DateTimeOffset.Now);
            }
            else if (project.RecognitionStatus == RegisterRecognitionStatus.Processing)
            {
                project.RecognitionCancellationRequested = true;
                project.RecognitionUpdatedAt = DateTimeOffset.Now;
                project.StatusMessage = "Запрошена отмена выполняющегося распознавания.";
                cancelRunning = true;
            }
            else
            {
                throw new InvalidOperationException("Отменить можно только ожидающее или выполняющееся задание.");
            }
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        if (cancelRunning)
        {
            _cancellationRegistry.Cancel(projectId);
        }
    }

    /// <inheritdoc />
    public async Task MoveRecognitionAsync(
        string projectId,
        int direction,
        CancellationToken cancellationToken = default)
    {
        if (direction != -1 && direction != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var pending = new List<WeightRegisterProject>();
            foreach (var directory in Directory.EnumerateDirectories(_storageRoot))
            {
                var candidateId = Path.GetFileName(directory);
                if (!IsProjectId(candidateId))
                {
                    continue;
                }
                try
                {
                    var candidate = await LoadProjectCoreAsync(candidateId, cancellationToken);
                    if (candidate?.RecognitionStatus == RegisterRecognitionStatus.Pending)
                    {
                        pending.Add(candidate);
                    }
                }
                catch (JsonException)
                {
                    // Повреждённый проект не мешает изменить порядок остальных заданий.
                }
            }

            pending = pending.OrderBy(QueueSortValue).ToList();
            var currentIndex = pending.FindIndex(project => project.Id == projectId);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException("Менять порядок можно только для ожидающих заданий.");
            }
            var destinationIndex = currentIndex + direction;
            if (destinationIndex < 0 || destinationIndex >= pending.Count)
            {
                return;
            }

            (pending[currentIndex], pending[destinationIndex]) = (pending[destinationIndex], pending[currentIndex]);
            var firstOrder = pending.Min(QueueSortValue);
            for (var index = 0; index < pending.Count; index++)
            {
                pending[index].RecognitionQueueOrder = firstOrder + index;
                pending[index].RecognitionUpdatedAt = DateTimeOffset.Now;
                await SaveProjectCoreAsync(pending[index], cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task FinishRecognitionCancellationAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            if (project.RecognitionStatus is RegisterRecognitionStatus.Pending or RegisterRecognitionStatus.Processing)
            {
                SetRecognitionCancelled(project, DateTimeOffset.Now);
                await SaveProjectCoreAsync(project, cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task FailRecognitionAsync(
        string projectId,
        string message,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            project.RecognitionStatus = RegisterRecognitionStatus.Failed;
            var finishedAt = DateTimeOffset.Now;
            project.RecognitionFinishedAt = finishedAt;
            project.RecognitionUpdatedAt = finishedAt;
            project.RecognitionCurrentSheet = null;
            var safeMessage = message?.Trim();
            project.StatusMessage = string.IsNullOrWhiteSpace(safeMessage)
                ? "Распознавание завершилось с ошибкой."
                : safeMessage[..Math.Min(safeMessage.Length, 1000)];
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ImportRecognitionAsync(
        string projectId,
        Stream json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        var recognized = await JsonSerializer.DeserializeAsync<WeightRegisterProject>(json, JsonOptions, cancellationToken)
            ?? throw new JsonException("Файл распознавания пуст.");
        await ImportRecognitionCoreAsync(projectId, recognized, allowProcessing: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CompleteRecognitionAsync(
        string projectId,
        WeightRegisterProject recognized,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognized);
        return ImportRecognitionCoreAsync(projectId, recognized, allowProcessing: true, cancellationToken);
    }

    private async Task<bool> ImportRecognitionCoreAsync(
        string projectId,
        WeightRegisterProject recognized,
        bool allowProcessing,
        CancellationToken cancellationToken)
    {
        if (recognized.SchemaVersion is < 1 or > 2 || recognized.Sheets is null || recognized.Sheets.Count == 0)
        {
            throw new JsonException("Ожидался проект WeightRegisterReviewApp schema v1/v2 с непустым массивом sheets.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            if (allowProcessing && project.RecognitionCancellationRequested)
            {
                SetRecognitionCancelled(project, DateTimeOffset.Now);
                await SaveProjectCoreAsync(project, cancellationToken);
                return false;
            }
            var targetSheets = project.Sheets.Where(sheet => !sheet.IsRecognitionComplete).ToArray();
            if (targetSheets.Length == 0 && !allowProcessing)
            {
                // Сохраняем прежнюю возможность повторно импортировать исправленный JSON всего проекта.
                targetSheets = project.Sheets.ToArray();
            }
            if (targetSheets.Length == 0)
            {
                throw new InvalidOperationException("В проекте нет новых листов для импорта результата.");
            }
            if (targetSheets.Any(sheet => !sheet.IsCalibrationConfirmed || sheet.Calibration is null))
            {
                throw new InvalidOperationException("Перед импортом результата сохраните калибровку новых листов.");
            }
            if (!allowProcessing
                && _automaticRecognitionEnabled
                && project.RecognitionStatus == RegisterRecognitionStatus.Processing)
            {
                throw new InvalidOperationException(
                    "Автоматическое распознавание уже выполняется. Дождитесь его завершения или повторите ручной импорт после ошибки.");
            }

            var matchedSources = new HashSet<WeightRegisterSheet>();
            foreach (var target in targetSheets)
            {
                var source = FindMatchingSheet(recognized.Sheets, target, matchedSources);
                if (source is null)
                {
                    throw new JsonException($"В результате распознавания отсутствует лист {target.Name}.");
                }

                ValidateRecognizedSheet(project, recognized, target, source);
                matchedSources.Add(source);
                target.Cells = ReadRecognizedCells(source);
                ValidateRecognizedCells(target, target.Cells);
                var recognizedTotals = source.Totals
                    ?? throw new JsonException($"Лист {target.Name} содержит некорректный массив totals.");
                ValidateRecognizedTotals(target, recognizedTotals);
                target.Totals = recognizedTotals;
                target.SlaughterDate = source.SlaughterDate;
                target.CuttingDate = project.CuttingDate;
                target.IsRecognitionComplete = true;
                target.RecognitionWarnings = source.RecognitionWarnings?
                    .Where(warning => !string.IsNullOrWhiteSpace(warning))
                    .Select(warning => warning.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? new List<string>();
            }

            var knownSourceCount = recognized.Sheets.Count(source => project.Sheets.Any(target =>
                string.Equals(target.Id, source.Id, StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(target.SourceFile), Path.GetFileName(source.SourceFile), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(source.SourceFile), target.Name, StringComparison.OrdinalIgnoreCase)));
            if (knownSourceCount != recognized.Sheets.Count)
            {
                throw new JsonException("Результат содержит лишние или повторяющиеся листы, не соответствующие загруженным сканам.");
            }

            var finishedAt = DateTimeOffset.Now;
            project.RecognitionStatus = RegisterRecognitionStatus.Completed;
            project.RecognitionFinishedAt = finishedAt;
            project.RecognitionUpdatedAt = finishedAt;
            project.RecognitionCompletedSheets = targetSheets.Length;
            project.RecognitionTotalSheets = targetSheets.Length;
            project.RecognitionCurrentSheet = null;
            var warningCount = project.Sheets.Sum(sheet => sheet.RecognitionWarnings?.Count ?? 0);
            project.StatusMessage = warningCount > 0
                ? $"Распознавание завершено; выполнено сравнение. Предупреждений по заголовкам и качеству: {warningCount}."
                : allowProcessing
                    ? "AI-распознавание завершено; выполнено сравнение с автоматическим реестром."
                    : "Распознавание импортировано; выполнено сравнение с автоматическим реестром.";
            await SaveProjectCoreAsync(project, cancellationToken);

            var importedPath = Path.Combine(GetProjectDirectory(projectId), "recognition-result.json");
            await File.WriteAllTextAsync(
                importedPath,
                JsonSerializer.Serialize(recognized, JsonOptions),
                cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static long QueueSortValue(WeightRegisterProject project)
        => project.RecognitionQueueOrder
            ?? (project.RecognitionQueuedAt is { } queuedAt
                ? queuedAt.UtcDateTime.Ticks
                : project.CreatedAt.UtcDateTime.Ticks);

    private static void SetRecognitionCancelled(WeightRegisterProject project, DateTimeOffset cancelledAt)
    {
        project.RecognitionStatus = RegisterRecognitionStatus.Cancelled;
        project.RecognitionCancellationRequested = false;
        project.RecognitionFinishedAt = cancelledAt;
        project.RecognitionUpdatedAt = cancelledAt;
        project.RecognitionCurrentSheet = null;
        project.StatusMessage = "Задание распознавания отменено.";
    }

    private static WeightRegisterSheet? FindMatchingSheet(
        IEnumerable<WeightRegisterSheet> candidates,
        WeightRegisterSheet target,
        ISet<WeightRegisterSheet> alreadyMatched)
    {
        var byId = candidates.FirstOrDefault(item =>
            !alreadyMatched.Contains(item)
            && !string.IsNullOrWhiteSpace(item.Id)
            && string.Equals(item.Id, target.Id, StringComparison.Ordinal));
        if (byId is not null)
        {
            return byId;
        }

        var targetFileName = Path.GetFileName(target.SourceFile);
        var targetOriginalName = target.Name;
        return candidates.FirstOrDefault(item =>
            !alreadyMatched.Contains(item)
            && (string.Equals(
                    Path.GetFileName(item.SourceFile),
                    targetFileName,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileNameWithoutExtension(item.SourceFile),
                    targetOriginalName,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static void ValidateRecognizedSheet(
        WeightRegisterProject project,
        WeightRegisterProject recognized,
        WeightRegisterSheet target,
        WeightRegisterSheet source)
    {
        var sourceDate = source.CuttingDate;
        if (!sourceDate.HasValue && recognized.CuttingDate != default)
        {
            sourceDate = recognized.CuttingDate;
        }
        if (!sourceDate.HasValue || sourceDate.Value != project.CuttingDate)
        {
            throw new JsonException(
                $"Дата разделки листа {target.Name} не совпадает с проектом {project.CuttingDate:dd.MM.yyyy}.");
        }
        if (!source.SlaughterDate.HasValue)
        {
            throw new JsonException($"На листе {target.Name} отсутствует дата забоя.");
        }
        if (source.SlaughterDate.Value > project.CuttingDate)
        {
            throw new JsonException(
                $"Дата забоя листа {target.Name} не может быть позже даты разделки {project.CuttingDate:dd.MM.yyyy}.");
        }

        if (source.Table is null
            || source.Table.DataRows != target.Table.DataRows
            || source.Table.Columns != target.Table.Columns)
        {
            throw new JsonException(
                $"Размер таблицы листа {target.Name} должен быть {target.Table.DataRows}x{target.Table.Columns}.");
        }
    }

    private static List<RegisterReviewCell> ReadRecognizedCells(WeightRegisterSheet source)
    {
        if (source.Cells is { Count: > 0 })
        {
            return source.Cells;
        }
        if (source.LegacyData is null)
        {
            throw new JsonException($"Лист {source.Name} не содержит ни cells, ни data.");
        }
        if (source.LegacyData.Count != source.Table.DataRows
            || source.LegacyData.Any(row => row is null || row.Count != source.Table.Columns))
        {
            throw new JsonException(
                $"Массив data листа {source.Name} должен иметь размер {source.Table.DataRows}x{source.Table.Columns}.");
        }

        var cells = new List<RegisterReviewCell>();
        for (var row = 0; row < source.LegacyData.Count; row++)
        {
            var legacyRow = source.LegacyData[row]
                ?? throw new JsonException($"Строка {row + 1} листа {source.Name} отсутствует.");
            for (var column = 0; column < legacyRow.Count; column++)
            {
                var ocrText = legacyRow[column]?.Trim();
                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    continue;
                }

                cells.Add(new RegisterReviewCell
                {
                    Row = row + 1,
                    Column = column + 1,
                    OcrText = ocrText,
                    Value = ParseLegacyRegisterValue(ocrText),
                    Status = RegisterCellStatus.Unreviewed
                });
            }
        }

        return cells;
    }

    private static decimal ParseLegacyRegisterValue(string text)
    {
        var normalized = text.Replace(',', '.');
        if (!decimal.TryParse(
                normalized,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new JsonException($"Не удалось преобразовать значение ячейки '{text}'.");
        }

        return normalized.Contains('.', StringComparison.Ordinal) ? value : value / 100m;
    }

    private static void ValidateRecognizedCells(
        WeightRegisterSheet target,
        IReadOnlyCollection<RegisterReviewCell> cells)
    {
        if (cells.Count == 0)
        {
            throw new JsonException($"На листе {target.Name} не найдено ни одной распознанной ячейки.");
        }

        var coordinates = new HashSet<(int Row, int Column)>();
        foreach (var cell in cells)
        {
            if (cell.Row < 1 || cell.Row > target.Table.DataRows
                || cell.Column < 1 || cell.Column > target.Table.Columns)
            {
                throw new JsonException($"Ячейка {cell.Row}:{cell.Column} листа {target.Name} находится вне таблицы.");
            }
            if (!coordinates.Add((cell.Row, cell.Column)))
            {
                throw new JsonException($"Ячейка {cell.Row}:{cell.Column} листа {target.Name} повторяется.");
            }
            if (cell.Status == RegisterCellStatus.Empty
                && (cell.EffectiveValue.HasValue || !string.IsNullOrWhiteSpace(cell.OcrText)))
            {
                throw new JsonException(
                    $"Ячейка {cell.Row}:{cell.Column} листа {target.Name} одновременно помечена пустой и содержит значение.");
            }
            if (cell.Status == RegisterCellStatus.Suspect
                && (string.IsNullOrWhiteSpace(cell.Comment) || cell.Comment.Length < 12))
            {
                throw new JsonException(
                    $"Для сомнительной ячейки {cell.Row}:{cell.Column} листа {target.Name} не указана причина.");
            }
        }

        var occupiedColumns = cells.GroupBy(cell => cell.Column).ToArray();
        var expectedPositions = occupiedColumns.Sum(group => group.Max(cell => cell.Row));
        var coveredPositions = occupiedColumns.Sum(group => group
            .Where(cell => cell.Status != RegisterCellStatus.Empty)
            .Select(cell => cell.Row)
            .Distinct()
            .Count());
        var unexpectedEmptyPercent = (expectedPositions - coveredPositions) * 100d / expectedPositions;
        if (unexpectedEmptyPercent > 2d)
        {
            throw new JsonException(
                $"На листе {target.Name} найдено {unexpectedEmptyPercent:0.0}% пропусков внутри заполненных частей колонок.");
        }
    }

    private static void ValidateRecognizedTotals(
        WeightRegisterSheet target,
        IReadOnlyCollection<RegisterReviewTotal> totals)
    {
        var coordinates = new HashSet<(int Row, int Column)>();
        foreach (var total in totals)
        {
            if (total.Row < 1 || total.Row > target.Table.TotalRows
                || total.Column < 1 || total.Column > target.Table.Columns)
            {
                throw new JsonException($"Итог {total.Row}:{total.Column} листа {target.Name} находится вне таблицы.");
            }
            if (!coordinates.Add((total.Row, total.Column)))
            {
                throw new JsonException($"Итог {total.Row}:{total.Column} листа {target.Name} повторяется.");
            }
            if (total.Status == RegisterCellStatus.Suspect
                && (string.IsNullOrWhiteSpace(total.Comment) || total.Comment.Length < 12))
            {
                throw new JsonException(
                    $"Для сомнительного итога {total.Row}:{total.Column} листа {target.Name} не указана причина.");
            }
        }

        var occupiedColumns = target.Cells
            .Select(cell => cell.Column)
            .Distinct()
            .ToHashSet();
        var primaryTotalColumns = totals
            .Where(total => total.Row == 1)
            .Select(total => total.Column)
            .ToHashSet();
        if (!occupiedColumns.IsSubsetOf(primaryTotalColumns))
        {
            throw new JsonException(
                $"На листе {target.Name} отсутствует весовой итог первой строки для занятой колонки.");
        }
    }

    /// <inheritdoc />
    public async Task CorrectCellAsync(
        string projectId,
        string sheetId,
        int row,
        int column,
        decimal? correctedValue,
        string? editedBy,
        CancellationToken cancellationToken = default)
    {
        if (correctedValue.HasValue
            && (correctedValue.Value <= 0m
                || correctedValue.Value > 100m
                || correctedValue.Value * 100m % 2m != 0m))
        {
            throw new ArgumentOutOfRangeException(
                nameof(correctedValue),
                "Вес должен быть больше 0, не превышать 100 кг и соответствовать шагу 0,02 кг.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            var sheet = project.Sheets.SingleOrDefault(item => item.Id == sheetId)
                ?? throw new KeyNotFoundException("Лист проекта не найден.");
            if (row < 1 || row > sheet.Table.DataRows || column < 1 || column > sheet.Table.Columns)
            {
                throw new ArgumentOutOfRangeException(nameof(row), "Координаты находятся вне таблицы.");
            }

            var cell = sheet.Cells.SingleOrDefault(item => item.Row == row && item.Column == column);
            if (cell is null)
            {
                cell = new RegisterReviewCell { Row = row, Column = column, Status = RegisterCellStatus.Empty };
                sheet.Cells.Add(cell);
            }

            var oldValue = cell.EffectiveValue;
            if (correctedValue.HasValue)
            {
                cell.CorrectedValue = correctedValue;
                cell.Status = RegisterCellStatus.Corrected;
            }
            else
            {
                cell.Value = null;
                cell.CorrectedValue = null;
                cell.Status = RegisterCellStatus.Empty;
            }
            project.CorrectionLog.Add(new RegisterCorrectionLogEntry
            {
                SheetId = sheetId,
                Row = row,
                Column = column,
                OldValue = oldValue,
                NewValue = correctedValue,
                EditedBy = editedBy,
                Timestamp = DateTimeOffset.Now
            });
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CorrectTotalAsync(
        string projectId,
        string sheetId,
        int totalRow,
        int column,
        decimal? correctedValue,
        string? editedBy,
        CancellationToken cancellationToken = default)
    {
        if (correctedValue.HasValue
            && (correctedValue.Value <= 0m
                || correctedValue.Value > 10000m
                || correctedValue.Value * 100m % 2m != 0m))
        {
            throw new ArgumentOutOfRangeException(
                nameof(correctedValue),
                "Итог должен быть больше 0, не превышать 10000 кг и соответствовать шагу 0,02 кг.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var project = await RequireProjectAsync(projectId, cancellationToken);
            var sheet = project.Sheets.SingleOrDefault(item => item.Id == sheetId)
                ?? throw new KeyNotFoundException("Лист проекта не найден.");
            if (totalRow < 1 || totalRow > sheet.Table.TotalRows
                || column < 1 || column > sheet.Table.Columns)
            {
                throw new ArgumentOutOfRangeException(nameof(totalRow), "Координаты итога находятся вне таблицы.");
            }

            var total = sheet.Totals.SingleOrDefault(item => item.Row == totalRow && item.Column == column);
            if (total is null)
            {
                total = new RegisterReviewTotal
                {
                    Row = totalRow,
                    Column = column,
                    Status = RegisterCellStatus.Empty
                };
                sheet.Totals.Add(total);
            }

            var oldValue = total.EffectiveValue;
            if (correctedValue.HasValue)
            {
                total.CorrectedValue = correctedValue;
                total.Status = RegisterCellStatus.Corrected;
            }
            else
            {
                total.Value = null;
                total.CorrectedValue = null;
                total.Status = RegisterCellStatus.Empty;
            }

            project.CorrectionLog.Add(new RegisterCorrectionLogEntry
            {
                SheetId = sheetId,
                Row = sheet.Table.DataRows + totalRow,
                Column = column,
                IsTotal = true,
                OldValue = oldValue,
                NewValue = correctedValue,
                EditedBy = editedBy,
                Timestamp = DateTimeOffset.Now
            });
            await SaveProjectCoreAsync(project, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RegisterImageFile?> OpenImageAsync(
        string projectId,
        string sheetId,
        CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectCoreAsync(projectId, cancellationToken);
        var sheet = project?.Sheets.SingleOrDefault(item => item.Id == sheetId);
        if (sheet is null)
        {
            return null;
        }

        var projectDirectory = GetProjectDirectory(projectId);
        var imagePath = Path.GetFullPath(Path.Combine(projectDirectory, sheet.SourceFile.Replace('/', Path.DirectorySeparatorChar)));
        if (!imagePath.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(imagePath))
        {
            return null;
        }

        var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new RegisterImageFile(stream, GetContentType(imagePath));
    }

    /// <inheritdoc />
    public string GetRecognitionRequestPath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), "recognition-request.json");

    private async Task<WeightRegisterProject> RequireProjectAsync(string projectId, CancellationToken cancellationToken)
        => await LoadProjectCoreAsync(projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Проект ручного реестра не найден.");

    private async Task<WeightRegisterProject?> LoadProjectCoreAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetProjectDirectory(projectId), "project.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            FileOptions.Asynchronous);
        var project = await JsonSerializer.DeserializeAsync<WeightRegisterProject>(stream, JsonOptions, cancellationToken);
        if (project?.RecognitionStatus == RegisterRecognitionStatus.Completed
            && project.Sheets.All(sheet => !sheet.IsRecognitionComplete))
        {
            // Совместимость с проектами, сохранёнными до появления статуса отдельного листа.
            foreach (var sheet in project.Sheets)
            {
                sheet.IsRecognitionComplete = true;
            }
        }
        return project;
    }

    private async Task SaveProjectCoreAsync(WeightRegisterProject project, CancellationToken cancellationToken)
    {
        var directory = GetProjectDirectory(project.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "project.json");
        var temporaryPath = Path.Combine(directory, $"project.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Временный файл можно удалить при следующем обслуживании каталога.
            }
            catch (UnauthorizedAccessException)
            {
                // Не подменяем исходную ошибку записи ошибкой очистки.
            }
        }
    }

    private string GetProjectDirectory(string projectId)
    {
        if (!IsProjectId(projectId))
        {
            throw new ArgumentException("Некорректный идентификатор проекта.", nameof(projectId));
        }
        return Path.Combine(_storageRoot, projectId);
    }

    private static bool IsProjectId(string? projectId)
        => Guid.TryParseExact(projectId, "N", out _);

    private static void ValidateCalibration(RegisterTableCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (calibration.ImageWidth <= 0 || calibration.ImageHeight <= 0
            || calibration.TableLeft < 0 || calibration.TableTop < 0
            || calibration.TableRight <= calibration.TableLeft
            || calibration.TableBottom <= calibration.TableTop
            || calibration.TableRight > calibration.ImageWidth
            || calibration.TableBottom > calibration.ImageHeight
            || Math.Abs(calibration.RotationDegrees) > 5)
        {
            throw new ArgumentException("Границы калибровки находятся вне изображения или имеют неверный порядок.", nameof(calibration));
        }
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "scan" : cleaned;
    }

    private static string GetContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

    private static (int Width, int Height) ReadImageDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (stream.Read(header) == header.Length
            && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return (
                BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4)));
        }

        stream.Position = 0;
        if (stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD8)
        {
            while (stream.Position < stream.Length)
            {
                if (stream.ReadByte() != 0xFF)
                {
                    continue;
                }

                int marker;
                do
                {
                    marker = stream.ReadByte();
                }
                while (marker == 0xFF);

                Span<byte> lengthBytes = stackalloc byte[2];
                if (stream.Read(lengthBytes) != 2)
                {
                    break;
                }
                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                {
                    Span<byte> size = stackalloc byte[5];
                    if (stream.Read(size) != size.Length)
                    {
                        break;
                    }
                    return (
                        BinaryPrimitives.ReadUInt16BigEndian(size.Slice(3, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(size.Slice(1, 2)));
                }
                stream.Position += Math.Max(0, segmentLength - 2);
            }
        }

        throw new InvalidDataException($"Не удалось определить размер изображения {Path.GetFileName(path)}. Используйте корректный JPG или PNG.");
    }

    private sealed class FlexibleDateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private static readonly string[] Formats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy" };

        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return DateOnly.TryParseExact(
                value,
                Formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date)
                    ? date
                    : throw new JsonException($"Не удалось прочитать дату {value}.");
        }

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }
}
