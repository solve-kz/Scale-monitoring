using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Scalemon.Common.Auth;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Scalemon.ApiService.Controllers
{
    [ApiController]
    [Route("api/auth")] // <-- Все адреса в этом контроллере будут начинаться с /api/auth
    [IgnoreAntiforgeryToken]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        // Модель для приёма данных из JavaScript
        public class LoginModel
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginModel model)
        {
            if (model is null)
            {
                return Unauthorized();
            }

            var validation = await _authService.ValidateAsync(model.Username, model.Password);
            if (!validation.Ok)
            {
                return Unauthorized();
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, model.Username),
            };

            if (!string.IsNullOrWhiteSpace(validation.DisplayName))
            {
                claims.Add(new Claim(ClaimTypes.GivenName, validation.DisplayName!));
            }

            foreach (var role in validation.Roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity));

            return Ok();
        }

        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok();
        }
    }
}