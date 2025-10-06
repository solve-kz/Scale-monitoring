using System.Collections.Generic;

namespace Scalemon.Common.Auth;

public interface IAuthService
{
    Task<AuthValidationResult> ValidateAsync(string login, string password);
}

public sealed record AuthValidationResult(bool Ok, IReadOnlyCollection<string> Roles, string? DisplayName);

public sealed record UserRecord(string Login, string DisplayName, IReadOnlyCollection<string> Roles);

public interface IUsersStore
{
    IReadOnlyCollection<UserRecord> List();
    bool TryAdd(string login, string password, string displayName, IReadOnlyCollection<string> roles);
    bool TryUpdate(string login, string? password, string? displayName, IReadOnlyCollection<string>? roles);
    bool Remove(string login);
}
