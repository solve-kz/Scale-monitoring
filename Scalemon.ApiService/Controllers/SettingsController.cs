using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalemon.Common;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.ApiService.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly IOptionsMonitor<ServiceSettings> _settings; // <-- было ServiceSettings

    public SettingsController(LoggingLevelSwitch levelSwitch,
                              IOptionsMonitor<ServiceSettings> settings) // <-- было ServiceSettings
    {
        _levelSwitch = levelSwitch;
        _settings = settings;
    }

    [HttpGet("logging/levels")]
    public ActionResult<IEnumerable<string>> GetLoggingLevels()
        => Ok(Enum.GetNames(typeof(LogEventLevel)));

    public sealed record LogPaths(string MainLogPath, string DetailedLogPath);

    [HttpGet("logging/paths")]
    public ActionResult<LogPaths> GetLoggingPaths()
    {
        var s = _settings.CurrentValue;
        return Ok(new LogPaths(s.Logging.FilePath.MainLogPath,
                               s.Logging.FilePath.DetailedLogPath));
    }

    public sealed class SetLevelDto { public string? Level { get; set; } }

    [HttpPut("logging/level")]
    public IActionResult SetLoggingLevel([FromBody] SetLevelDto dto)
    {
        if (dto?.Level is null) return BadRequest("Level is required");
        if (!Enum.TryParse(dto.Level, true, out LogEventLevel lvl)) return BadRequest("Unknown level");
        _levelSwitch.MinimumLevel = lvl;
        return NoContent();
    }
}
