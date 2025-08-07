using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Scalemon.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scalemon.ApiService.Controllers
{
    [ApiController]
    [Route("api/service")]
    public class ServiceApiController : ControllerBase
    {
        private readonly ServiceSettings _svcSettings;
        private readonly string _settingsFilePath;

        public ServiceApiController(
            ServiceSettings svcSettings,
            IWebHostEnvironment env)
        {
            _svcSettings = svcSettings;
            _settingsFilePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        }

        /// <summary>
        /// GET /api/service/status
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            using var winSvc = new System.ServiceProcess.ServiceController(
                _svcSettings.Api.ServiceName);

            var status = winSvc.Status switch
            {
                System.ServiceProcess.ServiceControllerStatus.Running => "Running",
                System.ServiceProcess.ServiceControllerStatus.Paused => "Paused",
                _ => "Stopped"
            };

            return Ok(new { status });
        }

        /// <summary>
        /// GET /api/service/settings
        /// </summary>
        [HttpGet("settings")]
        public IActionResult GetSettings()
        {
            return Ok(new
            {
                apiSettings = _svcSettings.Api,
                authSettings = _svcSettings.Authentication,
                scaleSettings = _svcSettings.ScaleSettings,
                logSettings = _svcSettings.Logging,
                databaseSettings = _svcSettings.DatabaseSettings,
                systemSettings = _svcSettings.SystemSettings,
                plcSettings = _svcSettings.PlcSettings
            });
        }

        /// <summary>
        /// PUT /api/service/settings
        /// Сохраняет новые настройки в appsettings.json и возвращает 204 No Content.
        /// </summary>
        [HttpPut("settings")]
        public async Task<IActionResult> UpdateSettings([FromBody] SettingsDto dto)
        {
            // базовая валидация
            if (dto is null
                || dto.ApiSettings is null
                || dto.AuthenticationSettings is null
                || dto.ScaleSettings is null
                || dto.LogSettings is null
                || dto.DatabaseSettings is null
                || dto.SystemSettings is null
                || dto.PlcSettings is null)
            {
                return BadRequest("Нужно передать все разделы: apiSettings, authSettings, scaleSettings, logSettings, databaseSettings, systemSettings, plcSettings");
            }

            // 1. Прочитать весь файл
            string text = await System.IO.File.ReadAllTextAsync(_settingsFilePath);
            var root = JsonNode.Parse(text)?.AsObject();
            if (root == null)
                return BadRequest("Неверный формат appsettings.json");

            // 2. Заменить разделы
            root["Api"] = JsonNode.Parse(JsonSerializer.Serialize(dto.ApiSettings, new JsonSerializerOptions { WriteIndented = true }));
            root["Authentication"] = JsonNode.Parse(JsonSerializer.Serialize(dto.AuthenticationSettings, new JsonSerializerOptions { WriteIndented = true }));
            root["ScaleSettings"] = JsonNode.Parse(JsonSerializer.Serialize(dto.ScaleSettings, new JsonSerializerOptions { WriteIndented = true }));
            root["Logging"] = JsonNode.Parse(JsonSerializer.Serialize(dto.LogSettings, new JsonSerializerOptions { WriteIndented = true }));
            root["DatabaseSettings"] = JsonNode.Parse(JsonSerializer.Serialize(dto.DatabaseSettings, new JsonSerializerOptions { WriteIndented = true }));
            root["SystemSettings"] = JsonNode.Parse(JsonSerializer.Serialize(dto.SystemSettings, new JsonSerializerOptions { WriteIndented = true }));
            root["PlcSettings"] = JsonNode.Parse(JsonSerializer.Serialize(dto.PlcSettings, new JsonSerializerOptions { WriteIndented = true }));

            // 3. Записать обратно
            await System.IO.File.WriteAllTextAsync(
                _settingsFilePath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return NoContent();
        }

        // DTO для PUT
        public class SettingsDto
        {
            public ApiSettings ApiSettings { get; set; } = default!;
            public AuthenticationSettings AuthenticationSettings { get; set; } = default!;
            public ScaleSettings ScaleSettings { get; set; } = default!;
            public LoggingSettings LogSettings { get; set; } = default!;
            public DatabaseSettings DatabaseSettings { get; set; } = default!;
            public SystemSettings SystemSettings { get; set; } = default!;
            public PlcSettings PlcSettings { get; set; } = default!;
        }
    }
}
