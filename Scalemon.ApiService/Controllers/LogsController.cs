using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Scalemon.Common;

namespace Scalemon.ApiService.Controllers
{
    [ApiController]
    [Route("api")]
    public class LogsController : ControllerBase
    {
        private readonly ServiceSettings _svcSettings;
        private readonly string _mainLogPath;
        private readonly string _settingsFilePath;

        public LogsController(
            ServiceSettings svcSettings,
            IWebHostEnvironment env)
        {
            _svcSettings = svcSettings;
            // путь к основному файлу логов (может быть относительным)
            _mainLogPath = Path.Combine(env.ContentRootPath, _svcSettings.Logging.FilePath.MainLogPath.TrimStart('\\', '/'));
            _settingsFilePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        }

        /// <summary>
        /// 2.6 GET /api/logs?from={from}&to={to}&level={level}
        /// Возвращает записи лога по фильтрам.
        /// </summary>
        [HttpGet("logs")]
        public async Task<IActionResult> GetLogs(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string? level)
        {
            if (!System.IO.File.Exists(_mainLogPath))
                return NotFound($"Log file not found at {_mainLogPath}");

            var result = new List<LogEntry>();

            // Читаем файл построчно (поддержка одновременной записи)
            using var stream = new FileStream(_mainLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    // Предполагаем, что каждая строка — это JSON-объект LogEntry
                    var entry = JsonSerializer.Deserialize<LogEntry>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (entry == null) continue;

                    if (from.HasValue && entry.Timestamp < from.Value) continue;
                    if (to.HasValue && entry.Timestamp > to.Value) continue;
                    if (!string.IsNullOrEmpty(level) &&
                        !entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(entry);
                }
                catch
                {
                    // Не парсится — пропускаем
                }
            }

            return Ok(result);
        }

        /// <summary>
        /// 2.7 PUT /api/settings/logging/level
        /// Изменяет только Default‐уровень логирования в appsettings.json
        /// </summary>
        [HttpPut("settings/logging/level")]
        public async Task<IActionResult> UpdateLogLevel([FromBody] LevelDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Level))
                return BadRequest("Требуется поле level");

            // 1. Прочитать конфиг
            var text = await System.IO.File.ReadAllTextAsync(_settingsFilePath);
            var root = JsonNode.Parse(text)?.AsObject();
            if (root == null)
                return BadRequest("Неверный формат appsettings.json");

            // 2. Найти и обновить Logging:Level:Default
            var loggingSection = root["Logging"]?.AsObject();
            var levelSection = loggingSection?["Level"]?.AsObject();
            if (levelSection == null)
                return BadRequest("Секция Logging:Level не найдена в конфиге");

            levelSection["Default"] = dto.Level;

            // 3. Записать обратно
            await System.IO.File.WriteAllTextAsync(
                _settingsFilePath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            );

            // reloadOnChange подхватит новое значение автоматически
            return NoContent();
        }

        // --- вспомогательные DTO и модели ---

        /// <summary>
        /// Строка лога
        /// </summary>
        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Level { get; set; } = default!;
            public string Message { get; set; } = default!;
            public string? Exception { get; set; }
        }

        /// <summary>
        /// DTO для PUT /settings/logging/level
        /// </summary>
        public class LevelDto
        {
            public string Level { get; set; } = default!;
        }
    }
}
