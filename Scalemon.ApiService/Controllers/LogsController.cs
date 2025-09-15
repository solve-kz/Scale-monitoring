using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scalemon.ApiService.Controllers;

[ApiController]
[Route("api/logs")]
public sealed class LogsController : ControllerBase
{
    private readonly ILogger<LogsController> _logger;
    private readonly string _logPath;
    static readonly string[] LevelOrder = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };
    static int Rank(string s) => Array.IndexOf(LevelOrder, NormalizeLevel(s));

    public LogsController(IConfiguration config, ILogger<LogsController> logger)
    {
        _logger = logger;

        // Путь к единственному логу: из конфигурации или дефолт ./logs/main.log
        var fromConfig = config["Logging:FilePath:MainLogPath"];
        var def = Path.Combine(AppContext.BaseDirectory, "logs", "main.log");
        _logPath = string.IsNullOrWhiteSpace(fromConfig) ? def : fromConfig;
    }

    // Если пришёл path в query — используем его, иначе путь из конфигурации/дефолта
    private string ResolvePath(string? q) => string.IsNullOrWhiteSpace(q) ? _logPath : q;

    private static string ExpandRollingLogPath(string target)
    {
        if (System.IO.File.Exists(target)) return target;

        var dir = Path.GetDirectoryName(target);
        var baseName = Path.GetFileNameWithoutExtension(target); // "main"
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return target;

        try
        {
            var pick = Directory.GetFiles(dir, baseName + "*.log")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return pick?.FullName ?? target;
        }
        catch
        {
            return target;
        }
    }

    // 1) Универсальное чтение открытого файла
    private static async Task<string[]> ReadAllLinesUnlockedAsync(string path, CancellationToken ct)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>(1024);

        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await sr.ReadLineAsync();
            if (line is null) break;
            lines.Add(line);
        }
        return lines.ToArray();
    }

    // 2) Небольшой ретрай, чтобы сгладить момент «дописали — закрыли»
    private static async Task<string[]> TryReadAllLinesRobustAsync(string path, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            try { return await ReadAllLinesUnlockedAsync(path, ct); }
            catch (IOException) { await Task.Delay(50, ct); }             // файл на мгновение занят
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
        return Array.Empty<string>();
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


    private static IEnumerable<string> ExpandRollingLogPaths(string target)
    {
        if (System.IO.File.Exists(target)) { yield return target; yield break; }

        var dir = Path.GetDirectoryName(target);
        var baseName = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) yield break;

        IEnumerable<FileInfo> files = Enumerable.Empty<FileInfo>();
        try
        {
            files = Directory.EnumerateFiles(dir, baseName + "*.log")
                             .Select(p => new FileInfo(p))
                             .OrderByDescending(f => f.LastWriteTimeUtc);
        }
        catch { }

        foreach (var f in files)
            yield return f.FullName;
    }


    // -----------------------------------------------------------------------
    // МОДЕЛИ
    public sealed record LogEntry(DateTime Timestamp, string Level, string Source, string Message, string? Exception);
    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

    // -----------------------------------------------------------------------
    // БЫСТРЫЙ ПРЕДПРОСМОТР (для Settings.razor): первые 500 строк
    // GET /api/logs?path=...
    [HttpGet]
    public async Task<ActionResult<IEnumerable<LogEntry>>> Preview([FromQuery] string? path)
    {
        var target = ExpandRollingLogPath(ResolvePath(path));
        if (!System.IO.File.Exists(target))
            return Ok(Array.Empty<LogEntry>());

        var lines = await TryReadAllLinesRobustAsync(target, HttpContext.RequestAborted);
        var items = ParseMany(lines).Take(500).ToList();
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
        var ct = HttpContext.RequestAborted;

        // локальные границы (используем и для предварительного отбора файлов)
        DateTime? fromLocal = from?.ToLocalTime().DateTime;
        DateTime? toLocal = to?.ToLocalTime().DateTime;

        var targets = ExpandRollingLogPaths(ResolvePath(path))
            // лёгкий предфильтр по времени изменения файла (ускоряет выборку)
            .Select(p => new FileInfo(p))
            .Where(f => (!fromLocal.HasValue || f.LastWriteTime >= fromLocal.Value.Date) &&
                        (!toLocal.HasValue || f.LastWriteTime <= toLocal.Value.Date.AddDays(1)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .ToArray();

        if (targets.Length == 0)
            return Ok(new PagedResult<LogEntry>(Array.Empty<LogEntry>(), 0));

        var all = new List<LogEntry>(8192);
        foreach (var t in targets)
        {
            var lines = await TryReadAllLinesRobustAsync(t, ct);
            all.AddRange(ParseMany(lines));
        }

        IEnumerable<LogEntry> query = all;

        // уровни по возрастанию важности
        

        if (!string.IsNullOrWhiteSpace(levels))
        {
            var tokens = levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Select(NormalizeLevel)
                               .ToArray();

            if (tokens.Length == 1)
            {
                var min = Rank(tokens[0]);
                if (min >= 0)
                    query = query.Where(e => Rank(e.Level) >= min);
            }
            else
            {
                var allowed = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
                query = query.Where(e => allowed.Contains(NormalizeLevel(e.Level)));
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e =>
                (e.Message?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (e.Source?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (e.Exception?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

        if (fromLocal.HasValue) query = query.Where(e => e.Timestamp >= fromLocal.Value);
        if (toLocal.HasValue) query = query.Where(e => e.Timestamp <= toLocal.Value);

        query = query.OrderByDescending(e => e.Timestamp);

        var total = query.Count();
        var page = query.Skip(skip).Take(take).ToList();

        return Ok(new PagedResult<LogEntry>(page, total));
    }

    // -----------------------------------------------------------------------
    // ЭКСПОРТ CSV
    // GET /api/logs/export?path=..
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? path)
    {
        var targets = ExpandRollingLogPaths(ResolvePath(path)).ToArray();
        if (targets.Length == 0) return NotFound("Log file not found.");

        var entries = new List<LogEntry>(8192);
        foreach (var t in targets)
        {
            var lines = await System.IO.File.ReadAllLinesAsync(t);
            entries.AddRange(ParseMany(lines));
        }

        var csv = BuildCsv(entries);
        var name = $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        return File(csv, "text/csv", name);
    }

    // -----------------------------------------------------------------------
    // ОЧИСТКА
    // DELETE /api/logs?path=..
    [HttpDelete]
    public IActionResult Clear([FromQuery] string? path)
    {
        var target = ExpandRollingLogPath(ResolvePath(path));
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!System.IO.File.Exists(target))
            return NoContent(); // нечего чистить

        System.IO.File.WriteAllText(target, string.Empty);
        return NoContent();
    }

    // -----------------------------------------------------------------------
    // ПРОСТОЙ ПАРСЕР (универсальный, не привязан к формату; строка = Message)
    private static readonly Regex AnyIsoDate =
     new(@"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:\s*[+-]\d{2}:\d{2})?",
         RegexOptions.Compiled);

    private static IEnumerable<LogEntry> ParseMany(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? string.Empty;

            // 1) JSON Serilog
            if (TryParseSerilogJson(line, out var jsonEntry))
            {
                yield return jsonEntry;
                continue;
            }

            // 2) Текст Serilog: 2025-08-28 19:23:45.678 +05:00 [INF] Source: Message...
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
                // 3) Фоллбэк: ищем ISO-дату в любом месте строки
                var dm = AnyIsoDate.Match(line);
                if (dm.Success && DateTimeOffset.TryParse(dm.Value, CultureInfo.InvariantCulture,
                                                         DateTimeStyles.AllowWhiteSpaces, out var dto2))
                    ts = dto2.ToLocalTime().DateTime;
            }

            yield return new LogEntry(ts, level, source, message, ex);
        }
    }

    private static byte[] BuildCsv(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Level,Source,Message,Exception");
        foreach (var e in entries)
        {
            sb.Append(Escape(e.Timestamp.ToString("O"))).Append(',');
            sb.Append(Escape(e.Level)).Append(',');
            sb.Append(Escape(e.Source)).Append(',');
            sb.Append(Escape(e.Message)).Append(',');
            sb.AppendLine(Escape(e.Exception));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());

        static string Escape(string? s)
        {
            s ??= string.Empty;
            if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                return $"\"{s.Replace("\"", "\"\"")}\"";
            return s;
        }
    }
}
