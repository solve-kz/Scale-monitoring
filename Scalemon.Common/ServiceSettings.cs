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
        public double   MinWeight { get; set; } = 5.0;
        public double   HystWeight { get; set; } = 0.1;
        public int SemaphoreTimeMs { get; set; } = 4000; 
    }

    public class PlcSettings
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int ReconnectIntervalMs { get; set; } = 1000;
    }
}
