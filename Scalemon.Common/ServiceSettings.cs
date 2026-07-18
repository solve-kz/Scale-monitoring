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
        public string MainLogPath { get; set; } = @"C:\Logs\main.log";
        public string DetailedLogPath { get; set; } = @"C:\Logs\detailed.log";
    }

    public class LogDatabaseSettings
    {
        public string MainDatabasePath { get; set; } = @"C:\Logs\mainlogs.db";
    }

    public class DatabaseSettings
    {
        public int MaxRetryQueueSize { get; set; } = 100;
        public int AlarmSize { get; set; } = 50;
        public string ConnectionString { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
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
        /// Количество стабильных измерений, подтверждающих разгрузку платформы.
        /// </summary>
        public int UnloadStableSamples { get; set; } = 3;

        /// <summary>
        /// Тайм-аут одиночной команды ZERO/TARE, мс.
        /// </summary>
        public int CommandTimeoutMs { get; set; } = 500;

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
                throw new InvalidOperationException("Число измерений разгрузки должно быть в диапазоне 2..20.");
            if (PlateauWindowKg <= 0 || PlateauWindowKg >= (decimal)MinWeight)
                throw new InvalidOperationException("Окно плато должно быть больше нуля и меньше минимального веса продукции.");
            if (CommandTimeoutMs is < 100 or > 3000)
                throw new InvalidOperationException("Тайм-аут команды должен быть в диапазоне 100..3000 мс.");
        }
    }

    public class PlcSettings
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int ReconnectIntervalMs { get; set; } = 1000;
    }
}
