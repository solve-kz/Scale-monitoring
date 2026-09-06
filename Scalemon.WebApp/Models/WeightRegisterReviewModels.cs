using System.Text.Json.Serialization;
using Scalemon.Common;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Models;

/// <summary>Статус проверки распознанной ячейки.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegisterCellStatus
{
    Unreviewed,
    Reviewed,
    Corrected,
    Suspect,
    Empty
}

/// <summary>Состояние задания распознавания.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegisterRecognitionStatus
{
    CalibrationRequired,
    Ready,
    Pending,
    Processing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Результат сопоставления ручной и автоматической ячейки.</summary>
public enum RegisterComparisonKind
{
    Empty,
    WithinTolerance,
    Difference,
    ManualOnly,
    AutomaticOnlyBetween
}

/// <summary>Настройки рабочего процесса сверки ручных реестров.</summary>
public sealed class WeightRegisterReviewOptions
{
    public string StoragePath { get; set; } = @"C:\Scalemon\WeightRegisterReview";
    public long MaxUploadBytes { get; set; } = 50L * 1024 * 1024;
    public decimal ComparisonToleranceKg { get; set; } = 0.04m;
    public WeightRegisterRecognitionOptions Recognition { get; set; } = new();
    public VideoArchiveOptions VideoArchive { get; set; } = new();
}

/// <summary>Настройки фонового визуального распознавания ручных реестров.</summary>
public sealed class WeightRegisterRecognitionOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "https://api.openai.com/v1/responses";
    public string Model { get; set; } = "gpt-5.6";
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";
    public int RequestTimeoutSeconds { get; set; } = 300;
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxOutputTokens { get; set; } = 30000;
}

/// <summary>Настройки адаптера архива видеорегистратора.</summary>
public sealed class VideoArchiveOptions
{
    public bool Enabled { get; set; }
    public int PreRollSeconds { get; set; } = 1;
    public string? UrlTemplate { get; set; }
}

/// <summary>Проект проверки одной даты разделки.</summary>
public sealed class WeightRegisterProject
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateOnly CuttingDate { get; set; }
    public RegisterRecognitionStatus RecognitionStatus { get; set; } = RegisterRecognitionStatus.CalibrationRequired;
    public string? StatusMessage { get; set; }
    public int RecognitionAttempt { get; set; }
    public long? RecognitionQueueOrder { get; set; }
    public bool RecognitionCancellationRequested { get; set; }
    public DateTimeOffset? RecognitionQueuedAt { get; set; }
    public DateTimeOffset? RecognitionStartedAt { get; set; }
    public DateTimeOffset? RecognitionFinishedAt { get; set; }
    public DateTimeOffset? RecognitionUpdatedAt { get; set; }
    public int RecognitionCompletedSheets { get; set; }
    public int RecognitionTotalSheets { get; set; }
    public string? RecognitionCurrentSheet { get; set; }
    public List<WeightRegisterSheet> Sheets { get; set; } = new();
    public List<RegisterCorrectionLogEntry> CorrectionLog { get; set; } = new();
}

/// <summary>Один отсканированный лист ручного реестра.</summary>
public sealed class WeightRegisterSheet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFileAlias
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) SourceFile = value;
        }
    }

    [JsonPropertyName("slaughter_date")]
    public DateOnly? SlaughterDate { get; set; }

    [JsonPropertyName("slaughterDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? SlaughterDateAlias
    {
        get => null;
        set
        {
            if (value.HasValue) SlaughterDate = value;
        }
    }

    [JsonPropertyName("cutting_date")]
    public DateOnly? CuttingDate { get; set; }

    [JsonPropertyName("cuttingDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? CuttingDateAlias
    {
        get => null;
        set
        {
            if (value.HasValue) CuttingDate = value;
        }
    }

    public RegisterTableShape Table { get; set; } = new();
    public RegisterTableCalibration? Calibration { get; set; }
    public bool IsCalibrationConfirmed { get; set; }
    public bool IsRecognitionComplete { get; set; }
    public List<string> RecognitionWarnings { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => IsRecognitionComplete
        ? $"{Name} — распознан"
        : IsCalibrationConfirmed
            ? $"{Name} — готов"
            : $"{Name} — нужна калибровка";

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<string?>>? LegacyData { get; set; }

    public List<RegisterReviewCell> Cells { get; set; } = new();
    public List<RegisterReviewTotal> Totals { get; set; } = new();
}

/// <summary>Размерность таблицы на скане.</summary>
public sealed class RegisterTableShape
{
    public int HeaderRows { get; set; } = 2;
    public int DataRows { get; set; } = 40;
    public int TotalRows { get; set; } = 2;
    public int Columns { get; set; } = 10;
}

/// <summary>Калибровка таблицы в пикселях исходного изображения.</summary>
public sealed class RegisterTableCalibration
{
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public double TableLeft { get; set; }
    public double TableTop { get; set; }
    public double TableRight { get; set; }
    public double TableBottom { get; set; }
    public double RotationDegrees { get; set; }

    /// <summary>Создаёт независимую копию для формы редактирования.</summary>
    public RegisterTableCalibration Copy() => (RegisterTableCalibration)MemberwiseClone();
}

/// <summary>Распознанная ячейка ручного реестра.</summary>
public sealed class RegisterReviewCell
{
    public int Row { get; set; }
    public int Column { get; set; }
    public string? OcrText { get; set; }
    public decimal? Value { get; set; }
    public decimal? CorrectedValue { get; set; }
    public RegisterCellStatus Status { get; set; } = RegisterCellStatus.Unreviewed;
    public string? Comment { get; set; }
    public double? Confidence { get; set; }

    [JsonIgnore]
    public decimal? EffectiveValue => CorrectedValue ?? Value;
}

/// <summary>Распознанный рукописный итог колонки.</summary>
public sealed class RegisterReviewTotal
{
    public int Row { get; set; } = 1;
    public int Column { get; set; }
    public string? OcrText { get; set; }
    public decimal? Value { get; set; }
    public decimal? CorrectedValue { get; set; }
    public RegisterCellStatus Status { get; set; } = RegisterCellStatus.Unreviewed;
    public string? Comment { get; set; }

    [JsonIgnore]
    public decimal? EffectiveValue => CorrectedValue ?? Value;
}

/// <summary>Запись аудита ручной коррекции распознавания.</summary>
public sealed class RegisterCorrectionLogEntry
{
    public string SheetId { get; set; } = string.Empty;
    public int Row { get; set; }
    public int Column { get; set; }
    public bool IsTotal { get; set; }
    public decimal? OldValue { get; set; }
    public decimal? NewValue { get; set; }
    public string? EditedBy { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
}

/// <summary>Краткая запись для выбора сохранённого проекта.</summary>
public sealed record WeightRegisterProjectSummary(
    string Id,
    DateOnly CuttingDate,
    string ProjectName,
    DateTimeOffset CreatedAt,
    RegisterRecognitionStatus Status,
    int SheetCount)
{
    public string DisplayName => $"{CuttingDate:dd.MM.yyyy} — {ProjectName}";
}

/// <summary>Снимок файловой очереди и состояния фонового исполнителя распознавания.</summary>
public sealed record WeightRegisterRecognitionQueueInfo(
    int WaitingCount,
    int ProcessingCount,
    int? Position,
    int ActiveCount,
    bool AutomaticRecognitionEnabled,
    DateTimeOffset? WorkerLastSeenAt,
    string? WorkerMessage,
    IReadOnlyList<WeightRegisterRecognitionQueueItem> Items);

/// <summary>Задание, отображаемое в панели управления очередью распознавания.</summary>
public sealed record WeightRegisterRecognitionQueueItem(
    string ProjectId,
    string ProjectName,
    DateOnly CuttingDate,
    RegisterRecognitionStatus Status,
    int Position,
    DateTimeOffset? QueuedAt,
    bool CancellationRequested);

/// <summary>Загружаемый файл скана.</summary>
public sealed record RegisterUploadFile(string FileName, string ContentType, Stream Content, long Length);

/// <summary>Поток изображения и его MIME-тип.</summary>
public sealed record RegisterImageFile(Stream Content, string ContentType);

/// <summary>Ключ ячейки внутри проекта.</summary>
public readonly record struct RegisterCellKey(string SheetId, int Row, int Column);

/// <summary>Сопоставление одной ручной ячейки с автоматической записью.</summary>
public sealed class RegisterCellComparison
{
    public required RegisterCellKey Key { get; init; }
    public required RegisterReviewCell Manual { get; init; }
    public Weighing? Automatic { get; set; }
    public RegisterComparisonKind Kind { get; set; }
    public List<Weighing> AutomaticGaps { get; } = new();
}

/// <summary>Результат сопоставления проекта с автоматическим реестром.</summary>
public sealed class RegisterComparisonResult
{
    public Dictionary<RegisterCellKey, RegisterCellComparison> Cells { get; } = new();
    public int AutomaticOnlyCount { get; set; }
    public int ManualOnlyCount { get; set; }
}

/// <summary>Строка таблицы сверки 40×10.</summary>
public sealed record RegisterGridRowVm(int No, IReadOnlyDictionary<int, RegisterCellComparison?> Cells)
{
    /// <summary>Возвращает ячейку указанной колонки.</summary>
    public RegisterCellComparison? GetCell(int column)
        => Cells.TryGetValue(column, out var cell) ? cell : null;
}

/// <summary>Прямоугольник crop в пикселях исходного скана.</summary>
public sealed record RegisterCellBounds(double Left, double Top, double Width, double Height);

/// <summary>Ссылка на фрагмент видеоархива.</summary>
public sealed record VideoArchiveClip(
    bool IsConfigured,
    DateTime StartAt,
    int PreRollSeconds,
    string? Url,
    string Message);
