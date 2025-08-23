using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Scalemon.Common; // ServiceSettings и разделы
using System.Text.Json;
using System.Text.Json.Nodes;
using Scalemon.WebApp.Components.Pages;
using Scalemon.WebApp.Models;  // <— добавить


namespace Scalemon.WebApp
{
    public sealed class JsonFileSettingsSource : ISettingsSource
    {
        private readonly IWebHostEnvironment _env;
        private readonly WebAppOptions _opts;

        public sealed class WebAppOptions
        {
            public string SettingsSource { get; set; } = "Api";
            public string? SettingsFilePath { get; set; }
        }

        public JsonFileSettingsSource(IWebHostEnvironment env, IOptions<WebAppOptions> opts)
        {
            _env = env;
            _opts = opts.Value;
        }

        private string ResolvePath()
        {
            var path = _opts.SettingsFilePath ?? "appsettings.json";
            return Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, path));
        }

        public Task<SettingsDto> LoadAsync(CancellationToken ct = default)
        {
            var full = ResolvePath();

            // Самый простой способ – IConfiguration + Bind в готовый класс
            var cfg = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(full)!)
                .AddJsonFile(Path.GetFileName(full), optional: false, reloadOnChange: false)
                .Build();

            var svc = new ServiceSettings();
            cfg.Bind(svc);

            return Task.FromResult(MapToDto(svc));
        }

        public async Task SaveAsync(SettingsDto dto, CancellationToken ct = default)
        {
            var full = ResolvePath();

            // Сохраняем весь файл единым объектом (красиво отформатированный JSON)
            var svc = MapFromDto(dto);
            var json = JsonSerializer.Serialize(svc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(full, json, ct);
        }

        // --- маппинг между общим ServiceSettings и DTO страницы ---
        private static SettingsDto MapToDto(ServiceSettings s) => new SettingsDto
        {
            ApiSettings = s.Api ?? new ApiSettings(),
            AuthenticationSettings = s.Authentication ?? new AuthenticationSettings(),
            LogSettings = s.Logging ?? new LoggingSettings(),
            DatabaseSettings = s.DatabaseSettings ?? new DatabaseSettings(),
            ScaleSettings = s.ScaleSettings ?? new ScaleSettings(),
            SystemSettings = s.SystemSettings ?? new SystemSettings(),
            PlcSettings = s.PlcSettings ?? new PlcSettings()
        };

        private static ServiceSettings MapFromDto(SettingsDto d) => new ServiceSettings
        {
            Api = d.ApiSettings ?? new ApiSettings(),
            Authentication = d.AuthenticationSettings ?? new AuthenticationSettings(),
            Logging = d.LogSettings ?? new LoggingSettings(),
            DatabaseSettings = d.DatabaseSettings ?? new DatabaseSettings(),
            ScaleSettings = d.ScaleSettings ?? new ScaleSettings(),
            SystemSettings = d.SystemSettings ?? new SystemSettings(),
            PlcSettings = d.PlcSettings ?? new PlcSettings()
        };

    }

}
