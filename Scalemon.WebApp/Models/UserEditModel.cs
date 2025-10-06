using System.Collections.Generic;

namespace Scalemon.WebApp.Models;

public sealed class UserEditModel
{
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public string? Password { get; set; }
    public bool RequirePassword { get; set; }
    public bool ChangePassword { get; set; } = true;
}
