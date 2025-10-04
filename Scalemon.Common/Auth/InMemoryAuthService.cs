// using System.Collections.Concurrent;
// using System.Text.Json;

using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Scalemon.Common.Auth
{
    public interface IAuthService
    {
        Task<(bool ok, string? role)> ValidateAsync(string login, string password);
    }

    public record UserRecord(string Login, string Role);

    public interface IUsersStore
    {
        IReadOnlyCollection<UserRecord> List();
        bool TryAdd(string login, string password, string role);
        bool TryUpdate(string login, string? password, string? role);
        bool Remove(string login);
    }

    public sealed class InMemoryAuthService : IAuthService, IUsersStore
    {
        private readonly ConcurrentDictionary<string, (string pass, string role)> _users;
        private readonly string? _filePath;
        private readonly object _io = new();

        public InMemoryAuthService(IOptions<ServiceSettings> opt /* оставить и текущий ctor, если есть */)
        {
            _filePath = opt.Value.Authentication?.UsersFilePath;
            _users = Load(_filePath)
                ?? new ConcurrentDictionary<string, (string pass, string role)>(
                    new[]
                    {
                        new KeyValuePair<string,(string,string)>("viewer", ("viewer123","Viewer")),
                        new KeyValuePair<string,(string,string)>("editor", ("<ehuek.r","Editor")),
                        new KeyValuePair<string,(string,string)>("admin",  ("1<ehuek.r","Admin")),
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        public Task<(bool ok, string? role)> ValidateAsync(string login, string password) =>
            Task.FromResult(_users.TryGetValue(login, out var u) && u.pass == password
                ? (true, u.role)
                : (false, null));

        // --- IUsersStore ---
        public IReadOnlyCollection<UserRecord> List() =>
            _users.Select(kv => new UserRecord(kv.Key, kv.Value.role))
                  .OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase)
                  .ToArray();

        public bool TryAdd(string login, string password, string role)
        {
            var ok = _users.TryAdd(login, (password, role));
            if (ok) Save();
            return ok;
        }

        public bool TryUpdate(string login, string? password, string? role)
        {
            if (!_users.TryGetValue(login, out var cur)) return false;
            _users[login] = (password ?? cur.pass, role ?? cur.role);
            Save();
            return true;
        }

        public bool Remove(string login)
        {
            var ok = _users.TryRemove(login, out _);
            if (ok) Save();
            return ok;
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_filePath)) return;
            lock (_io)
            {
                var payload = _users.Select(kv => new UserFile { login = kv.Key, password = kv.Value.pass, role = kv.Value.role });
                File.WriteAllText(_filePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private static ConcurrentDictionary<string, (string pass, string role)>? Load(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var arr = JsonSerializer.Deserialize<List<UserFile>>(File.ReadAllText(path)) ?? new();
                return new ConcurrentDictionary<string, (string, string)>(
                    arr.Select(i => new KeyValuePair<string, (string, string)>(i.login, (i.password, i.role))),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch { return null; }
        }

        private sealed class UserFile { public string login { get; set; } = ""; public string password { get; set; } = ""; public string role { get; set; } = "Viewer"; }
    }
}
