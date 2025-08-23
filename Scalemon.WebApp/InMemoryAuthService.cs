namespace Scalemon.WebApp
{
    public interface IAuthService
    {
        Task<(bool ok, string? role)> ValidateAsync(string login, string password);
    }

    public sealed class InMemoryAuthService : IAuthService
    {
        private readonly Dictionary<string, (string pass, string role)> _users = new()
        {
            ["viewer"] = ("viewer123", "Viewer"),
            ["editor"] = ("<ehuek.r", "Editor"),
            ["admin"] = ("1<ehuek.r", "Admin"),
        };

        public Task<(bool ok, string? role)> ValidateAsync(string login, string password)
            => Task.FromResult(_users.TryGetValue(login, out var u) && u.pass == password
                ? (true, u.role)
                : (false, null));
    }

}
