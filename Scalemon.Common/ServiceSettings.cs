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
        // по умолчанию 5000, но при биндинге возьмётся из конфигурации
        public int Port { get; set; } = 5000;        
        public string ServiceName { get; set; } = "ScalemonService"; 
    }

    public class AuthenticationSettings
    {
        public BasicAuthSettings Basic { get; set; } = new BasicAuthSettings();
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
