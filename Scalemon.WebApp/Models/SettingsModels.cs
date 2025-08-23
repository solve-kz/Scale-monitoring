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
    public UsersSettings UsersSettings { get; set; } = new();

    // НОВОЕ:
    public WebUiSettings WebUiSettings { get; set; } = default!;
}

public class UsersSettings
{
    public bool SqlMirror { get; set; } = true;
    public List<string> DefaultDbRoles { get; set; } = new();
    public int SessionTimeoutMinutes { get; set; } = 30;
    public List<UserDto> Users { get; set; } = new();
}

public class UserDto
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string>? Roles { get; set; }
    public List<string>? DbRolesOverride { get; set; }
}

public class LogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
}
