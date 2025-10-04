using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalemon.Common;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Scalemon.ApiService.Controllers
{
    [ApiController]
    [Route("api/auth")] // <-- Все адреса в этом контроллере будут начинаться с /api/auth
    public class AuthController : ControllerBase
    {
        private readonly ServiceSettings _settings;

        public AuthController(IOptions<ServiceSettings> settings)
        {
            _settings = settings.Value;
        }

        // Модель для приёма данных из JavaScript
        public class LoginModel
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginModel model)
        {
            var authUser = _settings.Authentication.Basic.Username;
            var authPass = _settings.Authentication.Basic.Password;

            // Проверяем логин и пароль из appsettings.json
            if (model.Username == authUser && model.Password == authPass)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, model.Username),
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                // Создаём аутентификационную cookie
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity));

                return Ok(); // Возвращаем HTTP 200 OK
            }

            return Unauthorized(); // Возвращаем HTTP 401 Unauthorized
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok();
        }
    }
}