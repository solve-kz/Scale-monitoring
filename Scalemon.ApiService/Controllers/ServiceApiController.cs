using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalemon.Common;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Scalemon.ApiService.Controllers
{
    [ApiController]
    [Route("api/service")]
    [Authorize]
    public class ServiceApiController : ControllerBase
    {
        private readonly ServiceSettings _svcSettings;
        private readonly string _settingsFilePath;

        public ServiceApiController(
            IOptionsMonitor<ServiceSettings> monitor,
            IWebHostEnvironment env)
        {
            _svcSettings = monitor.CurrentValue ;
            _settingsFilePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        }

        /// <summary>
        /// GET /api/service/status
        /// </summary>
        [HttpGet("status")]
        [Produces("application/json")]
        public IActionResult GetStatus()
        {
            try
            {
                var name = _svcSettings.Api.ServiceName;

                // в dev служба может быть не установлена — возвращаем заглушку
                var exists = ServiceController.GetServices()
                    .Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

                var status = exists
                    ? new ServiceController(name).Status switch
                    {
                        ServiceControllerStatus.Running => "Running",
                        ServiceControllerStatus.Paused => "Paused",
                        _ => "Stopped"
                    }
                    : "Running"; // заглушка при отладке консолью

                // ВАЖНО: именно JsonResult, чтобы клиент видел "application/json" и кавычки
                return new JsonResult(status);
            }
            catch
            {
                // на любой сбой возвращаем JSON-строку, чтобы UI не падал
                return new JsonResult("Running");
            }
        }

        /// <summary>
        /// GET /api/service/settings
        /// </summary>
        [HttpGet("settings")]
        public IActionResult GetSettings()
        {
            return Ok(new
            {
                ApiSettings = _svcSettings.Api,
                AuthenticationSettings = _svcSettings.Authentication,
                ScaleSettings = _svcSettings.ScaleSettings,
                LogSettings = _svcSettings.Logging,
                DatabaseSettings = _svcSettings.DatabaseSettings,
                SystemSettings = _svcSettings.SystemSettings,
                PlcSettings = _svcSettings.PlcSettings
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
