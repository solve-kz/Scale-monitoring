using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scalemon.Common;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.ApiService.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController : ControllerBase
{
    private readonly IOptionsMonitor<ServiceSettings> _opt;
    private readonly ISignalBus _signalBus;
    private readonly ILogger<DiagnosticsController> _logger;
    public DiagnosticsController(IOptionsMonitor<ServiceSettings> opt,
                                 ISignalBus signalBus,
                                 ILogger<DiagnosticsController> logger)
    {
        _opt = opt;
        _signalBus = signalBus;
        _logger = logger;
    }


    public sealed class DbTestDto
    {
        public string? ConnectionString { get; set; }
        public string? TableName { get; set; }
    }

    [HttpPost("db/test")]
    public async Task<IActionResult> TestDb([FromBody] DbTestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ConnectionString))
            return BadRequest("Строка подключения пуста");

        try
        {
            await using var conn = new SqlConnection(dto.ConnectionString);
            await conn.OpenAsync();

            if (!string.IsNullOrWhiteSpace(dto.TableName))
            {
                var sql = $"SELECT TOP (1) 1 FROM [{dto.TableName}]";
                await using var cmd = new SqlCommand(sql, conn);
                await cmd.ExecuteScalarAsync();
            }

            return Ok("Подключение успешно.");
        }
        catch (SqlException ex) { return Problem($"Ошибка SQL: {ex.Message}", statusCode: 500); }
        catch (Exception ex) { return Problem($"Ошибка подключения: {ex.Message}", statusCode: 500); }
    }

    [HttpGet("serialports")]
    public ActionResult<IEnumerable<string>> GetSerialPorts()
    {
        var ports = SerialPort.GetPortNames()
                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                              .ToArray();
        return Ok(ports);
    }

    [HttpGet("scale/test")]
    public async Task<IActionResult> TestScale(
    [FromServices] IScaleProcessor scales,
    [FromQuery] int timeoutMs = 3000)
    {
        var tcs = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Создаём один обработчик для нового события DataReceived
        Func<ScaleDataPoint, Task> onData = data =>
        {
            if (!data.IsConnected)
            {
                tcs.TrySetException(new InvalidOperationException("Connection lost"));
            }
            else if (data.IsAlarm)
            {
                tcs.TrySetException(new InvalidOperationException("Scale alarm"));
            }
            else if (data.IsStable) // Успех, только если вес стабилен и нет ошибок
            {
                tcs.TrySetResult(data.WeightKg);
            }
            return Task.CompletedTask;
        };

        // Подписываемся на единственное событие
        scales.DataReceived += onData;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

            var weight = await tcs.Task;
            return Ok(weight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (TaskCanceledException)
        {
            return Problem("Timeout waiting for stable weight.", statusCode: 504);
        }
        finally
        {
            // Очень важная отписка, чтобы не было утечек памяти
            scales.DataReceived -= onData;
        }
    }
    [HttpPost("plc/ping")]
    public async Task<IActionResult> PingPlc([FromServices] Scalemon.Common.ISignalBus bus)
    {
        // Команды ArduinoSignalCode заданы в общем enum (LinkOn, LinkOff и др.):contentReference[oaicite:4]{index=4}:contentReference[oaicite:5]{index=5}
        await bus.SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.LinkOn);
        await Task.Delay(100);
        await bus.SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.LinkOff);

        // SendAsync пишет байт в открытый SerialPort; если порт не открыт — просто ничего не отправит (без исключения).
        return Ok();
    }
}

