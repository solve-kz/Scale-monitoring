using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components; // для NavigationManager
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Radzen;
using Scalemon.Common;
using Scalemon.WebApp.Components;
using Scalemon.WebApp.Data;
using System.IO.Ports;
using System.Security.Claims;


namespace Scalemon.WebApp
{
    public class Program
    {
        public record LoginDto(string Username, string Password);
        public record ThemeDto(string Value);
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Razor Components (.NET 8) + интерактивный серверный рендеринг
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

            // Radzen (ThemeService, DialogService, NotificationService, ContextMenuService)
            builder.Services.AddRadzenComponents();

            // Если в приложении есть API-контроллеры (например, /api/diagnostics/*)
            builder.Services.AddControllers();

            builder.Services.AddHttpClient();               // для вызовов из компонентов

            // ДОБАВЬТЕ ЭТО:
            builder.Services.AddScoped(sp =>
            {
                var nav = sp.GetRequiredService<NavigationManager>();
                return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
            });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddCascadingAuthenticationState();

            // Cookie-аутентификация
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/";
                    options.AccessDeniedPath = "/";
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);

                    options.Events = new CookieAuthenticationEvents
                    {
                        OnRedirectToLogin = ctx =>
                        {
                            if (ctx.Request.Path.StartsWithSegments("/_blazor"))
                            { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
                            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
                        },
                        OnRedirectToAccessDenied = ctx =>
                        {
                            if (ctx.Request.Path.StartsWithSegments("/_blazor"))
                            { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
                            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
                        }
                    };
                });

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("CanViewMonitoring", p => p.RequireRole("Viewer", "Editor", "Admin"));
                options.AddPolicy("CanEdit", p => p.RequireRole("Editor", "Admin"));
                options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
            });

            builder.Services.AddRadzenCookieThemeService(options =>
            {
                options.Name = "MyApplicationTheme"; // The name of the cookie
                options.Duration = TimeSpan.FromDays(365); // The duration of the cookie
            });

            // Ваши сервисы домена
            
            builder.Services.AddSingleton<IAuthService, InMemoryAuthService>();

            // ------------------ ВЫБОР ИСТОЧНИКА НАСТРОЕК ------------------
            builder.Services.Configure<JsonFileSettingsSource.WebAppOptions>(
                builder.Configuration.GetSection("WebApp"));

            var src = builder.Configuration["WebApp:SettingsSource"];
            if (builder.Environment.IsDevelopment() &&
                string.Equals(src, "File", StringComparison.OrdinalIgnoreCase))
            {
                builder.Services.AddScoped<ISettingsSource, JsonFileSettingsSource>();
            }
            else
            {
                // временно тоже читаем из файла, пока нет ApiSettingsSource
                builder.Services.AddScoped<ISettingsSource, JsonFileSettingsSource>();
            }
            // ---------------------------------------------------------------
            // Сервис данных:
            builder.Services.AddScoped<IWeighingDataService, SqlWeighingDataService>();
            var app = builder.Build();



            app.Use(async (ctx, next) =>
            {
                try { await next(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{ctx.Request.Method}] {ctx.Request.Path} -> {ex}");
                    throw;
                }
            });

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            // Антифорджери – ОДИН раз и до маппинга эндпоинтов
            app.UseAntiforgery();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            // Логин/логаут – отключаем антифорджери только для них
            app.MapPost("/auth/login", async (Program.LoginDto dto, HttpContext http, IAuthService auth) =>
            {
                var (ok, role) = await auth.ValidateAsync(dto.Username, dto.Password);
                if (!ok) return Results.Unauthorized();
                var claims = new List<Claim> { new(ClaimTypes.Name, dto.Username), new(ClaimTypes.Role, role!) };
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                    new AuthenticationProperties { IsPersistent = true });
                return Results.Ok();
            }).DisableAntiforgery();

            app.MapPost("/auth/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Ok();
            }).DisableAntiforgery();

            // Временные заглушки API (опционально)
            app.MapGet("/api/diagnostics/serialports", () => System.IO.Ports.SerialPort.GetPortNames());
            app.MapGet("/api/service/status", () => Results.Ok("Dev mode: OK"));
            app.MapGet("/api/logs", (string? path) => Results.Ok(Array.Empty<object>()));
            // Минимальный API: реальная проверка подключения
            app.MapPost("/api/diagnostics/db/test", async ([FromBody] DbTestDto dto) =>
            {
                if (string.IsNullOrWhiteSpace(dto.ConnectionString))
                    return Results.BadRequest("Строка подключения пуста");

                try
                {
                    await using var conn = new SqlConnection(dto.ConnectionString);
                    await conn.OpenAsync(); // 1) проверили подключение

                    // 2) необязательно — проверим доступность таблицы
                    if (!string.IsNullOrWhiteSpace(dto.TableName))
                    {
                        var sql = $"SELECT TOP (1) 1 FROM [{dto.TableName}]";
                        await using var cmd = new SqlCommand(sql, conn);
                        await cmd.ExecuteScalarAsync();
                    }

                    return Results.Ok("Подключение успешно.");
                }
                catch (SqlException ex)
                {
                    return Results.Problem($"Ошибка SQL: {ex.Message}", statusCode: 500);
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Ошибка подключения: {ex.Message}", statusCode: 500);
                }
            });
            app.MapGet("/api/diagnostics/scale/test", () => Results.Ok("0.00"));
            app.MapPost("/api/diagnostics/plc/ping", () => Results.Ok());

            app.MapPost("/ui/theme", (HttpContext http, ThemeDto dto) =>
            {
                if (string.IsNullOrWhiteSpace(dto.Value)) return Results.BadRequest();

                http.Response.Cookies.Append(
                    "MyApplicationTheme",
                    dto.Value,
                    new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                        SameSite = SameSiteMode.Lax,
                        HttpOnly = false,
                        IsEssential = true,
                        Path = "/"
                    });

                return Results.NoContent();
            }).DisableAntiforgery();

            // Корневой компонент – МАППИНГ ОДИН РАЗ, В КОНЦЕ
            app.MapRazorComponents<App>()
               .AddInteractiveServerRenderMode();

            app.Run();
        }
    }

    public sealed class DbTestDto
    {
        public string? ConnectionString { get; set; }
        public string? TableName { get; set; }
    }
}
