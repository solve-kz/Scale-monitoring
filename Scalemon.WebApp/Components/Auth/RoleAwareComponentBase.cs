using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Scalemon.WebApp.Components.Auth;

internal static class RoleVisibilityEvaluator
{
    public static bool CanSee(ClaimsPrincipal? user, int level)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (level <= 1)
        {
            return true;
        }

        var roles = user.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return level switch
        {
            2 => roles.Contains("Admin") || roles.Contains("Editor"),
            3 => roles.Contains("Admin"),
            _ => false
        };
    }
}

internal sealed class RoleVisibilityState
{
    private ClaimsPrincipal? _currentUser;

    public ClaimsPrincipal? CurrentUser => _currentUser;

    public bool IsAuthenticated => _currentUser?.Identity?.IsAuthenticated ?? false;

    public bool CanSee(int level) => RoleVisibilityEvaluator.CanSee(_currentUser, level);

    public void Update(ClaimsPrincipal? user)
    {
        _currentUser = user;
    }
}

public abstract class RoleAwareComponentBase : ComponentBase
{
    private readonly RoleVisibilityState _visibility = new();

    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected ClaimsPrincipal? CurrentUser => _visibility.CurrentUser;

    protected bool IsAuthenticated => _visibility.IsAuthenticated;

    protected bool CanSee(int level) => _visibility.CanSee(level);

    protected override async Task OnParametersSetAsync()
    {
        if (AuthenticationStateTask is not null)
        {
            var state = await AuthenticationStateTask;
            _visibility.Update(state.User);
        }

        await base.OnParametersSetAsync();
    }
}

public abstract class RoleAwareLayoutComponentBase : LayoutComponentBase
{
    private readonly RoleVisibilityState _visibility = new();

    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected ClaimsPrincipal? CurrentUser => _visibility.CurrentUser;

    protected bool IsAuthenticated => _visibility.IsAuthenticated;

    protected bool CanSee(int level) => _visibility.CanSee(level);

    protected override async Task OnParametersSetAsync()
    {
        if (AuthenticationStateTask is not null)
        {
            var state = await AuthenticationStateTask;
            _visibility.Update(state.User);
        }

        await base.OnParametersSetAsync();
    }
}
