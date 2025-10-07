using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Scalemon.WebApp.Security;

/// <summary>
///     Simplified security service that mirrors the Radzen security API surface
///     for working with roles inside Blazor components.
/// </summary>
public sealed class SecurityService : IDisposable
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private ClaimsPrincipal _user = new(new ClaimsIdentity());
    private Task? _initializationTask;
    private bool _disposed;

    public SecurityService(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    public event Action? SecurityStateChanged;

    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SecurityService));
        }

        if (_initializationTask is null)
        {
            _initializationTask = InitializeCoreAsync();
        }

        await _initializationTask.ConfigureAwait(false);
    }

    public bool IsAuthenticated() => _user.Identity?.IsAuthenticated == true;

    public string? UserName => _user.Identity?.Name;

    public bool IsInRole(string role)
        => !string.IsNullOrWhiteSpace(role) && _user.IsInRole(role);

    public bool IsInAnyRole(params string[] roles)
    {
        if (roles is null || roles.Length == 0)
        {
            return false;
        }

        return roles.Any(IsInRole);
    }

    public Task<bool> IsInRoleAsync(string role)
        => Task.FromResult(IsInRole(role));

    public Task<bool> IsInAnyRoleAsync(params string[] roles)
        => Task.FromResult(IsInAnyRole(roles));

    public async Task<ClaimsPrincipal> GetUserAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        return _user;
    }

    public async Task RefreshAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        await UpdateCurrentUserAsync().ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync()
    {
        _authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
        await UpdateCurrentUserAsync().ConfigureAwait(false);
    }

    private async Task UpdateCurrentUserAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        SetUser(state.User);
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> task)
    {
        _ = UpdateFromAuthenticationStateAsync(task);
    }

    private async Task UpdateFromAuthenticationStateAsync(Task<AuthenticationState> task)
    {
        try
        {
            var state = await task.ConfigureAwait(false);
            SetUser(state.User);
        }
        catch
        {
            // ignored – if the task faults we keep the previous user
        }
    }

    private void SetUser(ClaimsPrincipal? user)
    {
        var previousAuthenticated = IsAuthenticated();
        _user = user ?? new ClaimsPrincipal(new ClaimsIdentity());

        if (previousAuthenticated != IsAuthenticated())
        {
            SecurityStateChanged?.Invoke();
            return;
        }

        SecurityStateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_initializationTask is not null)
        {
            _authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }

        _disposed = true;
    }
}
