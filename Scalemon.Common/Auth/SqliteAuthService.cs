using Scalemon.Common.Updates;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Scalemon.Common.Auth;

public sealed class SqliteAuthService : IAuthService, IUsersStore
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    private readonly ILogger<SqliteAuthService> _logger;
    private readonly string _connectionString;
    private readonly AuthenticationSettings _settings;
    private readonly object _initLock = new();
    private bool _initialized;

    public SqliteAuthService(IOptions<ServiceSettings> options, ILogger<SqliteAuthService> logger)
    {
        _settings = options.Value.Authentication ?? new AuthenticationSettings();
        _logger = logger;

        var path = _settings.UsersDatabasePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "users.db");
            path = File.Exists(legacy) ? legacy : InstallationPaths.DataFile("users.db");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
    }

    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.CompletedTask;
    }

    public async Task<AuthValidationResult> ValidateAsync(string login, string password)
    {
        EnsureInitialized();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash, salt, roles, display_name FROM users WHERE login = $login";
        command.Parameters.AddWithValue("$login", login);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new AuthValidationResult(false, Array.Empty<string>(), null);
        }

        var storedHash = reader.GetString(0);
        var salt = reader.GetString(1);
        var rolesJson = reader.GetString(2);
        var displayName = reader.GetString(3);

        if (!VerifyPassword(password, storedHash, salt))
        {
            return new AuthValidationResult(false, Array.Empty<string>(), null);
        }

        var roles = DeserializeRoles(rolesJson);
        return new AuthValidationResult(true, roles, displayName);
    }

    public IReadOnlyCollection<UserRecord> List()
    {
        EnsureInitialized();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT login, display_name, roles FROM users ORDER BY login COLLATE NOCASE";

        using var reader = command.ExecuteReader();
        var users = new List<UserRecord>();
        while (reader.Read())
        {
            var login = reader.GetString(0);
            var displayName = reader.GetString(1);
            var roles = DeserializeRoles(reader.GetString(2));
            users.Add(new UserRecord(login, displayName, roles));
        }

        return users;
    }

    public bool TryAdd(string login, string password, string displayName, IReadOnlyCollection<string> roles)
    {
        using var maintenanceOperation = MaintenanceGate.Shared.Enter();
        EnsureInitialized();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(1) FROM users WHERE login = $login";
        check.Parameters.AddWithValue("$login", login);
        var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        if (exists)
        {
            return false;
        }

        var salt = GenerateSalt();
        var hash = HashPassword(password, salt);
        var now = DateTimeOffset.UtcNow;
        var display = string.IsNullOrWhiteSpace(displayName) ? login : displayName;
        var rolesJson = SerializeRoles(roles);

        using var insert = connection.CreateCommand();
        insert.CommandText = @"INSERT INTO users (login, password_hash, salt, roles, display_name, created_at, updated_at)
                               VALUES ($login, $hash, $salt, $roles, $displayName, $created, NULL)";
        insert.Parameters.AddWithValue("$login", login);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$salt", salt);
        insert.Parameters.AddWithValue("$roles", rolesJson);
        insert.Parameters.AddWithValue("$displayName", display);
        insert.Parameters.AddWithValue("$created", now.UtcDateTime.ToString("O"));

        return insert.ExecuteNonQuery() > 0;
    }

    public bool TryUpdate(string login, string? password, string? displayName, IReadOnlyCollection<string>? roles)
    {
        using var maintenanceOperation = MaintenanceGate.Shared.Enter();
        EnsureInitialized();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT password_hash, salt, roles, display_name FROM users WHERE login = $login";
        select.Parameters.AddWithValue("$login", login);

        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var currentHash = reader.GetString(0);
        var currentSalt = reader.GetString(1);
        var currentRoles = reader.GetString(2);
        var currentDisplay = reader.GetString(3);

        reader.Close();

        var newSalt = currentSalt;
        var newHash = currentHash;
        if (!string.IsNullOrEmpty(password))
        {
            newSalt = GenerateSalt();
            newHash = HashPassword(password, newSalt);
        }

        var newRolesJson = roles is null ? currentRoles : SerializeRoles(roles);
        var newDisplay = string.IsNullOrWhiteSpace(displayName) ? currentDisplay : displayName!;
        var now = DateTimeOffset.UtcNow;

        using var update = connection.CreateCommand();
        update.CommandText = @"UPDATE users
                                SET password_hash = $hash,
                                    salt = $salt,
                                    roles = $roles,
                                    display_name = $display,
                                    updated_at = $updated
                                WHERE login = $login";
        update.Parameters.AddWithValue("$hash", newHash);
        update.Parameters.AddWithValue("$salt", newSalt);
        update.Parameters.AddWithValue("$roles", newRolesJson);
        update.Parameters.AddWithValue("$display", newDisplay);
        update.Parameters.AddWithValue("$updated", now.UtcDateTime.ToString("O"));
        update.Parameters.AddWithValue("$login", login);

        return update.ExecuteNonQuery() > 0;
    }

    public bool Remove(string login)
    {
        using var maintenanceOperation = MaintenanceGate.Shared.Enter();
        EnsureInitialized();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM users WHERE login = $login";
        delete.Parameters.AddWithValue("$login", login);

        return delete.ExecuteNonQuery() > 0;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS users (
                                        login TEXT PRIMARY KEY,
                                        password_hash TEXT NOT NULL,
                                        salt TEXT NOT NULL,
                                        roles TEXT NOT NULL,
                                        display_name TEXT NOT NULL,
                                        created_at TEXT NOT NULL,
                                        updated_at TEXT
                                    );";
                command.ExecuteNonQuery();
            }

            EnsureInitialAdmin(connection);
            _initialized = true;
        }
    }

    private void EnsureInitialAdmin(SqliteConnection connection)
    {
        var admin = _settings.InitialAdmin;
        if (admin is null || string.IsNullOrWhiteSpace(admin.Login) || string.IsNullOrEmpty(admin.Password))
        {
            return;
        }

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(1) FROM users WHERE login = $login";
        check.Parameters.AddWithValue("$login", admin.Login);
        var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        if (exists)
        {
            return;
        }

        var salt = GenerateSalt();
        var hash = HashPassword(admin.Password, salt);
        var now = DateTimeOffset.UtcNow;
        var roles = SerializeRoles(admin.Roles?.Length > 0 ? admin.Roles : new[] { "Admin" });
        var display = string.IsNullOrWhiteSpace(admin.DisplayName) ? admin.Login : admin.DisplayName;

        using var insert = connection.CreateCommand();
        insert.CommandText = @"INSERT INTO users (login, password_hash, salt, roles, display_name, created_at, updated_at)
                               VALUES ($login, $hash, $salt, $roles, $displayName, $created, NULL)";
        insert.Parameters.AddWithValue("$login", admin.Login);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$salt", salt);
        insert.Parameters.AddWithValue("$roles", roles);
        insert.Parameters.AddWithValue("$displayName", display);
        insert.Parameters.AddWithValue("$created", now.UtcDateTime.ToString("O"));
        insert.ExecuteNonQuery();

        _logger.LogInformation("Initial admin user '{Login}' created in authentication database", admin.Login);
    }

    private static string GenerateSalt()
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return Convert.ToBase64String(salt);
    }

    private static string HashPassword(string password, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, Iterations, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(pbkdf2.GetBytes(HashSize));
    }

    private static bool VerifyPassword(string password, string storedHash, string salt)
    {
        try
        {
            var computed = HashPassword(password, salt);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(storedHash),
                Convert.FromBase64String(computed));
        }
        catch
        {
            return false;
        }
    }

    private static string SerializeRoles(IReadOnlyCollection<string>? roles)
    {
        var normalized = roles?.Where(r => !string.IsNullOrWhiteSpace(r))
                               .Select(r => r.Trim())
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToArray() ?? Array.Empty<string>();
        return JsonSerializer.Serialize(normalized);
    }

    private static string[] DeserializeRoles(string json)
    {
        try
        {
            var roles = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return roles.Where(r => !string.IsNullOrWhiteSpace(r))
                        .Select(r => r.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
