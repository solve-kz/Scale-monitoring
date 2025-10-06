using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Scalemon.Common.Auth
{
    public sealed class InMemoryAuthService : IAuthService, IUsersStore
    {
        private readonly ConcurrentDictionary<string, (string pass, string displayName, string[] roles)> _users;
        private readonly string? _filePath;
        private readonly object _io = new();

        public InMemoryAuthService(IOptions<ServiceSettings> opt /* оставить и текущий ctor, если есть */)
        {
            _filePath = opt.Value.Authentication?.UsersFilePath;
            _users = Load(_filePath)
                ?? new ConcurrentDictionary<string, (string pass, string displayName, string[] roles)>(
                    new[]
                    {
                        new KeyValuePair<string,(string,string,string[])>("viewer", ("viewer123","Viewer", new[]{"Viewer"})),
                        new KeyValuePair<string,(string,string,string[])>("editor", ("<ehuek.r","Editor", new[]{"Editor"})),
                        new KeyValuePair<string,(string,string,string[])>("admin",  ("1<ehuek.r","Administrator", new[]{"Admin"})),
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        public Task<AuthValidationResult> ValidateAsync(string login, string password)
        {
            if (_users.TryGetValue(login, out var u) && u.pass == password)
            {
                return Task.FromResult(new AuthValidationResult(true, u.roles, u.displayName));
            }

            return Task.FromResult(new AuthValidationResult(false, Array.Empty<string>(), null));
        }

        public IReadOnlyCollection<UserRecord> List() =>
            _users.Select(kv => new UserRecord(kv.Key, kv.Value.displayName, kv.Value.roles))
                  .OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase)
                  .ToArray();

        public bool TryAdd(string login, string password, string displayName, IReadOnlyCollection<string> roles)
        {
            var normalizedRoles = NormalizeRoles(roles);
            var ok = _users.TryAdd(login, (password, string.IsNullOrWhiteSpace(displayName) ? login : displayName, normalizedRoles));
            if (ok) Save();
            return ok;
        }

        public bool TryUpdate(string login, string? password, string? displayName, IReadOnlyCollection<string>? roles)
        {
            if (!_users.TryGetValue(login, out var cur)) return false;
            var normalizedRoles = roles is null ? cur.roles : NormalizeRoles(roles, cur.roles);
            _users[login] = (password ?? cur.pass,
                             string.IsNullOrWhiteSpace(displayName) ? cur.displayName : displayName!,
                             normalizedRoles);
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
                var payload = _users.Select(kv => new UserFile
                {
                    login = kv.Key,
                    password = kv.Value.pass,
                    displayName = kv.Value.displayName,
                    roles = kv.Value.roles,
                });
                File.WriteAllText(_filePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private static ConcurrentDictionary<string, (string pass, string displayName, string[] roles)>? Load(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var arr = JsonSerializer.Deserialize<List<UserFile>>(File.ReadAllText(path)) ?? new();
                return new ConcurrentDictionary<string, (string, string, string[])>(
                    arr.Select(i => new KeyValuePair<string, (string, string, string[])>(
                        i.login,
                        (i.password,
                         string.IsNullOrWhiteSpace(i.displayName) ? i.login : i.displayName!,
                         NormalizeRoles(i.roles)))),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch { return null; }
        }

        private static string[] NormalizeRoles(IEnumerable<string>? roles, string[]? fallback = null)
        {
            var normalized = roles?.Where(r => !string.IsNullOrWhiteSpace(r))
                                   .Select(r => r.Trim())
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToArray();
            return normalized is { Length: > 0 } ? normalized : fallback ?? Array.Empty<string>();
        }

        private sealed class UserFile
        {
            public string login { get; set; } = "";
            public string password { get; set; } = "";
            public string? displayName { get; set; }
            public string[]? roles { get; set; }
        }
    }
}
