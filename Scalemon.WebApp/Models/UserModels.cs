using System.Collections.Generic;
using System.Linq;
using Scalemon.Common.Auth;

namespace Scalemon.WebApp.Models;

public sealed class UserListItem
{
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();

    public static UserListItem FromRecord(UserRecord record) => new()
    {
        Login = record.Login,
        DisplayName = record.DisplayName,
        Roles = record.Roles?.ToList() ?? new List<string>()
    };

    public static UserListItem FromSummary(ApiClient.UserSummary summary) => new()
    {
        Login = summary.Login,
        DisplayName = summary.DisplayName,
        Roles = summary.Roles?.ToList() ?? new List<string>()
    };
}

public sealed class UserEditModel
{
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
}
