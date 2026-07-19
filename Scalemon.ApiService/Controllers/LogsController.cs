using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Diagnostics;
using Scalemon.ApiService.Models;
using Scalemon.ApiService.Services;

namespace Scalemon.ApiService.Controllers;

[ApiController]
[Route("api/logs")]
public sealed class LogsController : ControllerBase
{
    private readonly ILogger<LogsController> _logger;
    private readonly SqliteLogRepository _repository;
    static readonly string[] LevelOrder = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };
    static int Rank(string s) => Array.IndexOf(LevelOrder, NormalizeLevel(s));

    public LogsController(SqliteLogRepository repository, ILogger<LogsController> logger)
    {
        _logger = logger;
        _repository = repository;
    }

    private static string NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return "";
        return level switch
        {
            "VRB" or "Verbose" => "Verbose",
            "DBG" or "Debug" => "Debug",
            "INF" or "Information" => "Information",
            "WRN" or "Warning" => "Warning",
            "ERR" or "Error" => "Error",
            "FTL" or "Fatal" => "Fatal",
            _ => level
        };
    }

    // 2) парсер строки (Serilog File sink по умолчанию):
    // 2025-08-28 19:23:45.678 +05:00 [INF] SourceContext: Message...
    private static readonly Regex SerilogLine =
        new(@"^\s*(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:\s*[+-]\d{2}:\d{2})?)\s*\[(?<lvl>[A-Z]{3,4}|Verbose|Debug|Information|Warning|Error|Fatal)\]\s*(?<src>[^:\-]*?)\s*(?:[:\-]\s*)?(?<msg>.*)$",
            RegexOptions.Compiled);



    // --- JSON парсер Serilog (compact JSON / default JSON)
    private static bool TryParseSerilogJson(string line, out LogEntry entry)
    {
        entry = default!;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;

            // timestamp: "Timestamp" или "@t"
            DateTime ts = DateTime.Now;
            if (root.TryGetProperty("Timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(tsEl.GetString(), out var dto1))
                ts = dto1.ToLocalTime().DateTime;
            else if (root.TryGetProperty("@t", out var ct) && ct.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(ct.GetString(), out var dto2))
                ts = dto2.ToLocalTime().DateTime;

            // level: "Level" или "@l"
            var level = ReadString(root, "Level") ?? ReadString(root, "@l") ?? "";

            // источник: чаще всего Properties.SourceContext
            string source = ReadString(root, "Source")
                         ?? ReadFromProps(root, "SourceContext")
                         ?? "";

            // сообщение: RenderedMessage -> @m -> Message -> MessageTemplate(+Properties)
            var message = ReadString(root, "RenderedMessage")
                       ?? ReadString(root, "@m")
                       ?? ReadString(root, "Message");

            if (string.IsNullOrEmpty(message))
            {
                var mt = ReadString(root, "MessageTemplate") ?? "";
                message = RenderFromTemplate(mt, root.TryGetProperty("Properties", out var p) ? p : default);
            }

            var ex = ReadString(root, "Exception") ?? ReadString(root, "@x");

            entry = new LogEntry(ts, NormalizeLevel(level), source, message ?? "", ex);
            return true;
        }
        catch
        {
            return false;
        }

        static string? ReadString(System.Text.Json.JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;

        static string? ReadFromProps(System.Text.Json.JsonElement root, string name)
        {
            if (root.TryGetProperty("Properties", out var props) &&
                props.ValueKind == System.Text.Json.JsonValueKind.Object &&
                props.TryGetProperty(name, out var el))
            {
                return el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : el.ToString();
            }
            return null;
        }

        // Простейший рендер для {Name}, {Name:format}; покрывает 99% ваших сообщений
        static string RenderFromTemplate(string template, System.Text.Json.JsonElement props)
        {
            if (string.IsNullOrWhiteSpace(template) || props.ValueKind != System.Text.Json.JsonValueKind.Object)
                return template ?? "";

            return System.Text.RegularExpressions.Regex.Replace(
                template,
                @"\{(?<name>[\w]+)(?:[^}]*)\}",
                m => {
                    var name = m.Groups["name"].Value;
                    if (props.TryGetProperty(name, out var val))
                        return JsonScalarToString(val);
                    return m.Value; // оставляем как есть
                });
        }

        static string JsonScalarToString(System.Text.Json.JsonElement el) =>
            el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => el.GetString()!,
                System.Text.Json.JsonValueKind.Number => el.ToString(),
                System.Text.Json.JsonValueKind.True => "True",
                System.Text.Json.JsonValueKind.False => "False",
                _ => el.ToString()
            };
    }
    // -----------------------------------------------------------------------
    // БЫСТРЫЙ ПРЕДПРОСМОТР (для Settings.razor): первые 500 строк
    // GET /api/logs?path=...
    [HttpGet]
    public async Task<ActionResult<IEnumerable<LogEntry>>> Preview([FromQuery] string? path)
    {
        _ = path; // для обратной совместимости параметр остаётся, но не используется
        var items = await _repository.GetRecentAsync(500, HttpContext.RequestAborted);
        return Ok(items);
    }

    // -----------------------------------------------------------------------
    // ПОСТРАНИЧНО (для Logs.razor)
    // GET /api/logs/paged?skip=..&take=..&levels=..&search=..&from=..&to=..&path=..
    [HttpGet("paged")]
    public async Task<ActionResult<PagedResult<LogEntry>>> GetPaged(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? levels = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? path = null)
    {
        _ = path;
        var ct = HttpContext.RequestAborted;

        var fromLocal = from?.ToLocalTime().DateTime;
        var toLocal = to?.ToLocalTime().DateTime;
        var allowedLevels = BuildLevelFilter(levels);
        var searchTerm = string.IsNullOrWhiteSpace(search) ? null : search;

        var page = await _repository.GetPagedAsync(skip, take, allowedLevels, searchTerm, fromLocal, toLocal, ct);
        return Ok(page);
    }

    // -----------------------------------------------------------------------
    // ЭКСПОРТ CSV
    // GET /api/logs/export?path=..
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? path,
        [FromQuery] string? levels = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        _ = path;
        var ct = HttpContext.RequestAborted;

        var fromLocal = from?.ToLocalTime().DateTime;
        var toLocal = to?.ToLocalTime().DateTime;
        var allowedLevels = BuildLevelFilter(levels);
        var searchTerm = string.IsNullOrWhiteSpace(search) ? null : search;

        var downloadName = $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"scalemon-logs-{Guid.NewGuid():N}.csv");
        var temporaryFile = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var entries = _repository.StreamAllAsync(
                allowedLevels,
                searchTerm,
                fromLocal,
                toLocal,
                ct);
            var rowCount = await WriteCsvAsync(temporaryFile, entries, ct);
            temporaryFile.Position = 0;

            _logger.LogInformation(
                "Подготовлен экспорт логов: Rows={Rows}, SizeBytes={SizeBytes}, ElapsedMs={ElapsedMs}",
                rowCount,
                temporaryFile.Length,
                stopwatch.ElapsedMilliseconds);

            // MVC копирует FileStream в HTTP-ответ частями и затем закрывает его.
            // DeleteOnClose гарантирует удаление временного файла.
            return File(temporaryFile, "text/csv; charset=utf-8", downloadName);
        }
        catch
        {
            await temporaryFile.DisposeAsync();
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // ОЧИСТКА
    // DELETE /api/logs?path=..
    [HttpDelete]
    public async Task<IActionResult> Clear([FromQuery] string? path)
    {
        _ = path;
        await _repository.ClearAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    // -----------------------------------------------------------------------
    // ИМПОРТ ТЕКСТОВОГО ЛОГА В SQLite
    // POST /api/logs/import
    private const long MaxLogImportSizeBytes = 256L * 1024 * 1024;

    [HttpPost("import")]
    [RequestSizeLimit(MaxLogImportSizeBytes)]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = MaxLogImportSizeBytes)]
    public async Task<IActionResult> Import([FromForm] List<IFormFile> files, [FromForm] List<string>? fileNames)
    {
        if (files is null || files.Count == 0)
        {
            return BadRequest("No files uploaded.");
        }

        var ct = HttpContext.RequestAborted;
        var results = new List<ImportResponse>(files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var requestedName = fileNames is { Count: > 0 } && i < fileNames.Count ? fileNames[i] : null;

            try
            {
                var summary = await _repository.ImportAsync(ReadEntriesAsync(file, ct), file.FileName, requestedName, ct);
                results.Add(new ImportResponse(file.FileName, summary.DatabaseFileName, summary.DatabasePath, summary.ImportedCount, null));
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import uploaded log {File}", file.FileName);
                results.Add(new ImportResponse(file.FileName, null, null, 0, ex.Message));
            }
        }

        return Ok(results);
    }

    private static IReadOnlyList<string>? BuildLevelFilter(string? levels)
    {
        if (string.IsNullOrWhiteSpace(levels)) return null;

        var tokens = levels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLevel)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
            return null;

        if (tokens.Length == 1)
        {
            var min = Rank(tokens[0]);
            if (min < 0) return null;
            return LevelOrder.Skip(min).ToArray();
        }

        return tokens;
    }

    private const long MaxUploadedLogSize = 512L * 1024 * 1024;

    private async IAsyncEnumerable<LogEntry> ReadEntriesAsync(IFormFile upload, [EnumeratorCancellation] CancellationToken ct)
    {
        if (upload.Length > MaxUploadedLogSize)
        {
            throw new InvalidOperationException($"Uploaded file '{upload.FileName}' exceeds the maximum allowed size of {MaxUploadedLogSize} bytes.");
        }

        await using var stream = upload.OpenReadStream();
        using var sr = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await sr.ReadLineAsync();
            if (line is null)
                yield break;

            if (TryParseLine(line, out var entry))
                yield return entry;
        }
    }

    private sealed record ImportResponse(string File, string? Database, string? Path, int Imported, string? Error);

    // -----------------------------------------------------------------------
    // ПРОСТОЙ ПАРСЕР (универсальный, не привязан к формату; строка = Message)
    private static readonly Regex AnyIsoDate =
     new(@"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:\s*[+-]\d{2}:\d{2})?",
         RegexOptions.Compiled);

    private static IEnumerable<LogEntry> ParseMany(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            if (TryParseLine(raw, out var entry))
                yield return entry;
        }
    }

    private static bool TryParseLine(string? raw, out LogEntry entry)
    {
        var line = raw?.Trim() ?? string.Empty;

        if (TryParseSerilogJson(line, out entry))
            return true;

        var ts = DateTime.Now;
        var level = "";
        var source = "";
        var message = line;
        string? ex = null;

        var m = SerilogLine.Match(line);
        if (m.Success)
        {
            if (DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AllowWhiteSpaces, out var dto))
                ts = dto.ToLocalTime().DateTime;

            level = NormalizeLevel(m.Groups["lvl"].Value);
            source = m.Groups["src"].Value?.Trim() ?? "";
            message = m.Groups["msg"].Value ?? "";
        }
        else
        {
            var dm = AnyIsoDate.Match(line);
            if (dm.Success && DateTimeOffset.TryParse(dm.Value, CultureInfo.InvariantCulture,
                                                     DateTimeStyles.AllowWhiteSpaces, out var dto2))
                ts = dto2.ToLocalTime().DateTime;
        }

        entry = new LogEntry(ts, level, source, message, ex);
        return true;
    }

    private static async Task<long> WriteCsvAsync(
        Stream output,
        IAsyncEnumerable<LogEntry> entries,
        CancellationToken ct)
    {
        await using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 64 * 1024,
            leaveOpen: true);
        await writer.WriteLineAsync("Timestamp,Level,Source,Message,Exception".AsMemory(), ct);

        var row = new StringBuilder(512);
        long rowCount = 0;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            row.Clear();
            AppendCsvField(row, entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            row.Append(',');
            AppendCsvField(row, entry.Level);
            row.Append(',');
            AppendCsvField(row, entry.Source);
            row.Append(',');
            AppendCsvField(row, entry.Message);
            row.Append(',');
            AppendCsvField(row, entry.Exception);

            await writer.WriteLineAsync(row.ToString().AsMemory(), ct);
            rowCount++;
            if (rowCount % 4096 == 0)
            {
                await writer.FlushAsync(ct);
            }
        }

        await writer.FlushAsync(ct);
        return rowCount;
    }

    private static void AppendCsvField(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        if (!value.Contains('"') &&
            !value.Contains(',') &&
            !value.Contains('\n') &&
            !value.Contains('\r'))
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (var character in value)
        {
            if (character == '"')
                builder.Append("\"\"");
            else
                builder.Append(character);
        }
        builder.Append('"');
    }
}
