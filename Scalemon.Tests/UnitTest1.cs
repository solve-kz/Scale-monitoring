using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Scalemon.Common;
using Scalemon.Common.Auth;
using System;
using System.IO;
using System.Linq;

namespace Scalemon.Tests.Auth;

[TestFixture]
public class SqliteAuthServiceTests
{
    private string _databasePath = null!;
    private SqliteAuthService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"scalemon_users_{Guid.NewGuid():N}.db");

        var settings = new ServiceSettings
        {
            Authentication = new AuthenticationSettings
            {
                UsersDatabasePath = _databasePath,
                InitialAdmin = new InitialAdminSettings
                {
                    Login = "seedAdmin",
                    Password = "Admin123!",
                    DisplayName = "Seed Admin",
                    Roles = new[] { "Admin" }
                }
            }
        };

        _service = new SqliteAuthService(Options.Create(settings), NullLogger<SqliteAuthService>.Instance);
        _service.EnsureInitializedAsync().GetAwaiter().GetResult();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch
            {
                // ignore cleanup errors
            }
        }
    }

    /*[Test]
    public async Task ValidateAsyncReturnsSeedAdmin()
    {
        var result = await _service.ValidateAsync("seedAdmin", "Admin123!");
        Assert.That(result.Ok, Is.True);
        Assert.That(result.Roles, Does.Contain("Admin"));
        Assert.That(result.DisplayName, Is.EqualTo("Seed Admin"));
    }*/

    [Test]
    public void TryAdd_CreatesUserAndAllowsAuthentication()
    {
        var added = _service.TryAdd("user1", "Password!1", "User One", new[] { "Viewer", "Editor" });
        Assert.That(added, Is.True);

        var validation = _service.ValidateAsync("user1", "Password!1").GetAwaiter().GetResult();
        Assert.That(validation.Ok, Is.True);
        Assert.That(validation.Roles, Is.EquivalentTo(new[] { "Viewer", "Editor" }));
        Assert.That(validation.DisplayName, Is.EqualTo("User One"));
    }

    [Test]
    public void TryUpdate_ChangesPasswordDisplayNameAndRoles()
    {
        _service.TryAdd("user2", "InitialPass!", "Initial", new[] { "Viewer" });

        var updated = _service.TryUpdate("user2", "NewPass#1", "Updated User", new[] { "Editor", "Admin" });
        Assert.That(updated, Is.True);

        var oldValidation = _service.ValidateAsync("user2", "InitialPass!").GetAwaiter().GetResult();
        Assert.That(oldValidation.Ok, Is.False);

        var newValidation = _service.ValidateAsync("user2", "NewPass#1").GetAwaiter().GetResult();
        Assert.That(newValidation.Ok, Is.True);
        Assert.That(newValidation.Roles, Is.EquivalentTo(new[] { "Editor", "Admin" }));
        Assert.That(newValidation.DisplayName, Is.EqualTo("Updated User"));
    }

    [Test]
    public void Remove_DeletesUser()
    {
        _service.TryAdd("user3", "Pass@123", "ToRemove", new[] { "Viewer" });
        var removed = _service.Remove("user3");
        Assert.That(removed, Is.True);

        var users = _service.List();
        Assert.That(users.Any(u => u.Login.Equals("user3", StringComparison.OrdinalIgnoreCase)), Is.False);
    }
}

