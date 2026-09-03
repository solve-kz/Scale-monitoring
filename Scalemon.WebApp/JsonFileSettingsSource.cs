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
            PlcSettings = s.PlcSettings ?? new PlcSettings(),
            WeightRegisterReviewSettings = MapWeightRegisterReview(s.WeightRegisterReview)
        };

        private static ServiceSettings MapFromDto(SettingsDto d) => new ServiceSettings
        {
            Api = d.ApiSettings ?? new ApiSettings(),
            Authentication = d.AuthenticationSettings ?? new AuthenticationSettings(),
            Logging = d.LogSettings ?? new LoggingSettings(),
            DatabaseSettings = d.DatabaseSettings ?? new DatabaseSettings(),
            ScaleSettings = d.ScaleSettings ?? new ScaleSettings(),
            SystemSettings = d.SystemSettings ?? new SystemSettings(),
            PlcSettings = d.PlcSettings ?? new PlcSettings(),
            WeightRegisterReview = MapWeightRegisterReview(d.WeightRegisterReviewSettings)
        };

        private static WeightRegisterReviewOptions MapWeightRegisterReview(WeightRegisterReviewSettings? settings)
        {
            settings ??= new WeightRegisterReviewSettings();
            return new WeightRegisterReviewOptions
            {
                StoragePath = settings.StoragePath,
                MaxUploadBytes = settings.MaxUploadBytes,
                ComparisonToleranceKg = settings.ComparisonToleranceKg,
                Recognition = new WeightRegisterRecognitionOptions
                {
                    Enabled = settings.Recognition?.Enabled ?? false,
                    Endpoint = settings.Recognition?.Endpoint ?? "https://api.openai.com/v1/responses",
                    Model = settings.Recognition?.Model ?? "gpt-5.6",
                    ApiKeyEnvironmentVariable = settings.Recognition?.ApiKeyEnvironmentVariable ?? "OPENAI_API_KEY",
                    RequestTimeoutSeconds = settings.Recognition?.RequestTimeoutSeconds ?? 300,
                    PollIntervalSeconds = settings.Recognition?.PollIntervalSeconds ?? 5,
                    MaxOutputTokens = settings.Recognition?.MaxOutputTokens ?? 30000
                },
                VideoArchive = new VideoArchiveOptions
                {
                    Enabled = settings.VideoArchive?.Enabled ?? false,
                    PreRollSeconds = settings.VideoArchive?.PreRollSeconds ?? 1,
                    UrlTemplate = settings.VideoArchive?.UrlTemplate
                }
            };
        }

        private static WeightRegisterReviewSettings MapWeightRegisterReview(WeightRegisterReviewOptions? settings)
        {
            settings ??= new WeightRegisterReviewOptions();
            return new WeightRegisterReviewSettings
            {
                StoragePath = settings.StoragePath,
                MaxUploadBytes = settings.MaxUploadBytes,
                ComparisonToleranceKg = settings.ComparisonToleranceKg,
                Recognition = new WeightRegisterRecognitionSettings
                {
                    Enabled = settings.Recognition?.Enabled ?? false,
                    Endpoint = settings.Recognition?.Endpoint ?? "https://api.openai.com/v1/responses",
                    Model = settings.Recognition?.Model ?? "gpt-5.6",
                    ApiKeyEnvironmentVariable = settings.Recognition?.ApiKeyEnvironmentVariable ?? "OPENAI_API_KEY",
                    RequestTimeoutSeconds = settings.Recognition?.RequestTimeoutSeconds ?? 300,
                    PollIntervalSeconds = settings.Recognition?.PollIntervalSeconds ?? 5,
                    MaxOutputTokens = settings.Recognition?.MaxOutputTokens ?? 30000
                },
                VideoArchive = new VideoArchiveSettings
                {
                    Enabled = settings.VideoArchive?.Enabled ?? false,
                    PreRollSeconds = settings.VideoArchive?.PreRollSeconds ?? 1,
                    UrlTemplate = settings.VideoArchive?.UrlTemplate
                }
            };
        }

    }

}
