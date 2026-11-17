using Scalemon.Common.Updates;
namespace Scalemon.Common
{
    // «Корневой» класс, в который будет биндиться весь JSON
    public class ServiceSettings
    {
        public ApiSettings Api { get; set; } = new ApiSettings();
        public AuthenticationSettings Authentication { get; set; } = new AuthenticationSettings();
        public LoggingSettings Logging { get; set; } = new LoggingSettings();
        public DatabaseSettings DatabaseSettings { get; set; } = new DatabaseSettings();
        public ScaleSettings ScaleSettings { get; set; } = new ScaleSettings();
        public SystemSettings SystemSettings { get; set; } = new SystemSettings();
        public PlcSettings PlcSettings { get; set; } = new PlcSettings();
        public WeightRegisterReviewSettings WeightRegisterReview { get; set; } = new WeightRegisterReviewSettings();
    }

    public class ApiSettings
    {
        public string Scheme { get; set; } = "http";       // NEW
        public string Host { get; set; } = "localhost";  // NEW
        // по умолчанию 5000, но при биндинге возьмётся из конфигурации
        public int Port { get; set; } = 5000;
        public string? BasePath { get; set; } = null;      // NEW (опционально)
        public string ServiceName { get; set; } = "ScalemonService"; 
    }

    public class AuthenticationSettings
    {
        public BasicAuthSettings Basic { get; set; } = new BasicAuthSettings();
        public string? UsersDatabasePath { get; set; }
        public InitialAdminSettings InitialAdmin { get; set; } = new InitialAdminSettings();
    }

    public class InitialAdminSettings
    {
        public string Login { get; set; } = "admin";
        public string Password { get; set; } = "ChangeMe!";
        public string DisplayName { get; set; } = "Administrator";
        public string[] Roles { get; set; } = new[] { "Admin" };
    }

    public class BasicAuthSettings
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoggingSettings
    {
        public LogLevelSettings Level { get; set; } = new LogLevelSettings();
        public LogFilePathSettings FilePath { get; set; } = new LogFilePathSettings();
        public LogDatabaseSettings Database { get; set; } = new LogDatabaseSettings();
    }

    public class LogLevelSettings
    {
        public string Default { get; set; } = "Information";
        public string Detailed { get; set; } = "Debug";
    }

    public class LogFilePathSettings
    {
        public string MainLogPath { get; set; } = InstallationPaths.LogFile("main.log");
        public string DetailedLogPath { get; set; } = InstallationPaths.LogFile("detailed.log");
    }

    public class LogDatabaseSettings
    {
        public string MainDatabasePath { get; set; } = InstallationPaths.LogFile("mainlogs.db");
    }

    public class DatabaseSettings
    {
        public int MaxRetryQueueSize { get; set; } = 100;
        public int AlarmSize { get; set; } = 50;
        public string ConnectionString { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;

        /// <summary>
        /// Путь к локальному SQLite-журналу режима «общий/санитарный».
        /// </summary>
        public string WeighingModeDatabasePath { get; set; } = InstallationPaths.DataFile("weighing-modes.db");
    }

    public class ScaleSettings
    {
        public string PortName { get; set; } = "COM2";
        public int PollingIntervalMs { get; set; } = 200;
        public int StableThreshold { get; set; } = 3;
        public int UnstableThreshold { get; set; } = 3;
        
    }

    public class SystemSettings
    {
        public double MinWeight { get; set; } = 5.0;

        // Оставлены для чтения старых файлов appsettings. Новая FSM их не использует.
        public double HystWeight { get; set; } = 0.1;
        public int SemaphoreTimeMs { get; set; } = 4000;

        /// <summary>
        /// Максимальный по модулю остаток, для которого сначала выполняется CMD_SET_ZERO.
        /// </summary>
        public decimal ZeroResidualMaxKg { get; set; } = 0.10m;

        /// <summary>
        /// Допустимое превышение порога ZERO для прямого тарирования, в процентах.
        /// </summary>
        public decimal TareAllowancePercent { get; set; } = 200m;

        /// <summary>
        /// Количество стабильных измерений, подтверждающих плато продукции.
        /// </summary>
        public int PlateauStableSamples { get; set; } = 3;

        /// <summary>
        /// Максимальный разброс измерений внутри подтверждаемого плато, кг.
        /// </summary>
        public decimal PlateauWindowKg { get; set; } = 0.04m;

        /// <summary>
        /// Количество стабильных измерений для подтверждения остатка перед ZERO/TARE.
        /// </summary>
        public int UnloadStableSamples { get; set; } = 3;

        /// <summary>
        /// Тайм-аут одиночной команды ZERO/TARE, мс.
        /// </summary>
        public int CommandTimeoutMs { get; set; } = 500;

        /// <summary>
        /// Максимальное число попыток ZERO в одном цикле коррекции, включая первую попытку.
        /// </summary>
        public int ZeroCommandMaxAttempts { get; set; } = 10;

        /// <summary>
        /// Максимальное число попыток TARE в одном цикле коррекции, включая первую попытку.
        /// </summary>
        public int TareCommandMaxAttempts { get; set; } = 5;

        /// <summary>
        /// Максимальное число успешных автоматических ZERO между подтверждёнными грузами.
        /// </summary>
        public int MaxAutomaticZeroApplications { get; set; } = 3;

        /// <summary>
        /// Максимальное число успешных автоматических TARE между подтверждёнными грузами.
        /// </summary>
        public int MaxAutomaticTareApplications { get; set; } = 2;

        /// <summary>
        /// Разрешает продолжать автофиксацию при защёлкнутой ошибке ZERO/TARE.
        /// Красная индикация при этом сохраняется до очистки и стабильного нуля.
        /// </summary>
        public bool EnableNonBlockingCorrection { get; set; } = true;

        /// <summary>
        /// Верхняя граница остатка, который разрешено компенсировать тарированием.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public decimal TareMaxKg => ZeroResidualMaxKg * (1m + TareAllowancePercent / 100m);

        /// <summary>
        /// Проверяет согласованность настроек автоматической фиксации.
        /// </summary>
        public void ValidateWeighing()
        {
            if (MinWeight <= 0)
                throw new InvalidOperationException("Минимальный вес продукции должен быть больше нуля.");
            if (ZeroResidualMaxKg <= 0)
                throw new InvalidOperationException("Максимум ZERO должен быть больше нуля.");
            if (TareAllowancePercent < 0 || TareAllowancePercent > 1000)
                throw new InvalidOperationException("Допуск TARE должен быть в диапазоне 0..1000 %.");
            if (TareMaxKg >= (decimal)MinWeight)
                throw new InvalidOperationException("Верхняя граница TARE должна быть меньше минимального веса продукции.");
            if (PlateauStableSamples is < 2 or > 20)
                throw new InvalidOperationException("Число измерений плато должно быть в диапазоне 2..20.");
            if (UnloadStableSamples is < 2 or > 20)
                throw new InvalidOperationException("Число стабильных измерений остатка должно быть в диапазоне 2..20.");
            if (PlateauWindowKg <= 0 || PlateauWindowKg >= (decimal)MinWeight)
                throw new InvalidOperationException("Окно плато должно быть больше нуля и меньше минимального веса продукции.");
            if (CommandTimeoutMs is < 100 or > 3000)
                throw new InvalidOperationException("Тайм-аут команды должен быть в диапазоне 100..3000 мс.");
            if (ZeroCommandMaxAttempts is < 1 or > 50)
                throw new InvalidOperationException("Число попыток ZERO должно быть в диапазоне 1..50.");
            if (TareCommandMaxAttempts is < 1 or > 20)
                throw new InvalidOperationException("Число попыток TARE должно быть в диапазоне 1..20.");
            if (MaxAutomaticZeroApplications is < 1 or > 100)
                throw new InvalidOperationException("Лимит автоматических ZERO должен быть в диапазоне 1..100.");
            if (MaxAutomaticTareApplications is < 1 or > 50)
                throw new InvalidOperationException("Лимит автоматических TARE должен быть в диапазоне 1..50.");
        }
    }

    public class PlcSettings
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int ReconnectIntervalMs { get; set; } = 1000;
    }

    public class WeightRegisterReviewSettings
    {
        public string StoragePath { get; set; } = InstallationPaths.DataFile("WeightRegisterReview");
        public long MaxUploadBytes { get; set; } = 50L * 1024 * 1024;
        public decimal ComparisonToleranceKg { get; set; } = 0.04m;
        public WeightRegisterRecognitionSettings Recognition { get; set; } = new WeightRegisterRecognitionSettings();
        public VideoArchiveSettings VideoArchive { get; set; } = new VideoArchiveSettings();
    }

    public class WeightRegisterRecognitionSettings
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = "https://api.openai.com/v1/responses";
        public string Model { get; set; } = "gpt-5.6";
        public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";
        public int RequestTimeoutSeconds { get; set; } = 300;
        public int PollIntervalSeconds { get; set; } = 5;
        public int MaxOutputTokens { get; set; } = 30000;
    }

    public class VideoArchiveSettings
    {
        public bool Enabled { get; set; }
        public int PreRollSeconds { get; set; } = 1;
        public string? UrlTemplate { get; set; }
    }
}
