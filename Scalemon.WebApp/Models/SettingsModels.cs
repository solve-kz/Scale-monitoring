namespace Scalemon.WebApp.Models;
using Scalemon.Common;

public class SettingsDto
{
    public ApiSettings ApiSettings { get; set; } = new();
    public AuthenticationSettings AuthenticationSettings { get; set; } = new();
    public LoggingSettings LogSettings { get; set; } = new();
    public DatabaseSettings DatabaseSettings { get; set; } = new();
    public ScaleSettings ScaleSettings { get; set; } = new();
    public SystemSettings SystemSettings { get; set; } = new();
    public PlcSettings PlcSettings { get; set; } = new();
    // НОВОЕ:
    public WebUiSettings WebUiSettings { get; set; } = default!;
}

public class LogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
}
