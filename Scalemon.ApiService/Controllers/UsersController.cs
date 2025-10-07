using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Scalemon.Common.Auth;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scalemon.ApiService.Controllers
{
    [ApiController]
    [Route("api/auth/users")]
    [Authorize(Roles = "Admin")]
    public class UsersController : ControllerBase
    {
        private readonly IUsersStore _usersStore;

        public UsersController(IUsersStore usersStore)
        {
            _usersStore = usersStore;
        }

        [HttpGet]
        public ActionResult<IEnumerable<UserDto>> GetUsers()
        {
            var users = _usersStore.List()
                .Select(u => new UserDto
                {
                    Login = u.Login,
                    DisplayName = u.DisplayName,
                    Roles = u.Roles.ToArray()
                })
                .ToArray();

            return Ok(users);
        }

        [HttpPost]
        public IActionResult CreateUser([FromBody] CreateUserDto dto)
        {
            if (dto is null)
            {
                return BadRequest("Request body required");
            }

            if (string.IsNullOrWhiteSpace(dto.Login) ||
                string.IsNullOrWhiteSpace(dto.Password) ||
                dto.Roles is null)
            {
                return BadRequest("login/password/roles required");
            }

            var roles = NormalizeRoles(dto.Roles);
            if (roles.Count == 0)
            {
                return BadRequest("At least one role required");
            }

            var created = _usersStore.TryAdd(dto.Login, dto.Password, dto.DisplayName ?? string.Empty, roles);
            if (!created)
            {
                return Conflict("User exists");
            }

            return Created($"/api/auth/users/{dto.Login}", null);
        }

        [HttpPut("{login}")]
        public IActionResult UpdateUser(string login, [FromBody] UpdateUserDto dto)
        {
            if (dto is null)
            {
                return BadRequest("Request body required");
            }

            var roles = dto.Roles is null ? null : NormalizeRoles(dto.Roles);
            var updated = _usersStore.TryUpdate(login, dto.Password, dto.DisplayName, roles);
            return updated ? NoContent() : NotFound();
        }

        [HttpDelete("{login}")]
        public IActionResult DeleteUser(string login)
        {
            var removed = _usersStore.Remove(login);
            return removed ? NoContent() : NotFound();
        }

        private static IReadOnlyCollection<string> NormalizeRoles(IEnumerable<string> roles)
        {
            return roles?
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }

        public sealed class UserDto
        {
            public string Login { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public IReadOnlyCollection<string> Roles { get; set; } = Array.Empty<string>();
        }

        public sealed class CreateUserDto
        {
            public string Login { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string? DisplayName { get; set; }
            public List<string> Roles { get; set; } = new();
        }

        public sealed class UpdateUserDto
        {
            public string? Password { get; set; }
            public string? DisplayName { get; set; }
            public List<string>? Roles { get; set; }
        }
    }
}
