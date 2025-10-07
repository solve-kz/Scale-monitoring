namespace Scalemon.WebApp.Components.Auth;

public sealed record LoginVm
{
    public string Login { get; set; } = "";
    public string Pass { get; set; } = "";
}
