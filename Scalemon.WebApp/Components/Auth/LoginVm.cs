namespace Scalemon.WebApp.Components.Auth;

public sealed record LoginVm
{
    public string Login { get; init; } = "";
    public string Pass { get; init; } = "";
}
