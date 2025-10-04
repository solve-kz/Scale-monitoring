using System.Net.Http.Json;
using System.Web;

namespace Scalemon.WebApp;

public sealed class ApiClient
{
    private readonly HttpClient _http;               // ← только внедрённый HttpClient

    public ApiClient(HttpClient http) => _http = http;

    // ---------- DTO/модели под ответы API ----------
    public sealed record DbTestDto(string? ConnectionString, string? TableName);
    public sealed record SetLevelDto(string Level);
    public sealed record LogPaths(string MainLogPath, string DetailedLogPath);
    public sealed record LogEntry(DateTime Timestamp, string Level, string Source, string Message, string? Exception);
    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

    // ---------- методы, которые вызывает UI ----------
    // /api/service/status  -> JSON-строка ("Running"/"Paused"/"Stopped")
    public async Task<string> GetServiceStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<string>("api/service/status", ct) ?? "Unknown";

    // /api/diagnostics/serialports
    public async Task<string[]> GetSerialPortsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<string[]>("api/diagnostics/serialports", ct) ?? Array.Empty<string>();

    // /api/diagnostics/db/test
    public async Task<string> TestDbAsync(string? conn, string? table, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/diagnostics/db/test", new DbTestDto(conn, table), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // /api/diagnostics/scale/test
    public Task<string> TestScaleAsync(CancellationToken ct = default) =>
        _http.GetStringAsync("api/diagnostics/scale/test", ct);

    // /api/diagnostics/plc/ping
    public async Task PingPlcAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync("api/diagnostics/plc/ping", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ---------- SettingsController: logging ----------
    // /api/settings/logging/levels
    public async Task<string[]> GetLoggingLevelsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<string[]>("api/settings/logging/levels", ct) ?? Array.Empty<string>();

    // /api/settings/logging/paths
    public Task<LogPaths?> GetLoggingPathsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<LogPaths>("api/settings/logging/paths", ct);

    // /api/settings/logging/level  (PUT { Level = "Information" | "Debug" | ... })
    public async Task SetLoggingLevelAsync(string level, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync("api/settings/logging/level", new SetLevelDto(level), ct);
        resp.EnsureSuccessStatusCode();
    }

    // ---------- LogsController ----------
    // Быстрый предпросмотр: GET /api/logs?path=...
    public async Task<IReadOnlyList<LogEntry>> GetLogPreviewAsync(string? path = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(path) ? "api/logs" : $"api/logs?path={Uri.EscapeDataString(path)}";
        return await _http.GetFromJsonAsync<IReadOnlyList<LogEntry>>(url, ct) ?? Array.Empty<LogEntry>();
    }

    // Пагинация: GET /api/logs/paged?skip=..&take=..&levels=..&search=..&from=..&to=..&path=..
    public async Task<PagedResult<LogEntry>> GetLogsPagedAsync(
        int skip = 0,
        int take = 50,
        string? levels = null,
        string? search = null,
        DateTime? from = null,
        DateTime? to = null,
        string? path = null,
        CancellationToken ct = default)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["skip"] = skip.ToString();
        q["take"] = take.ToString();
        if (!string.IsNullOrWhiteSpace(levels)) q["levels"] = levels;
        if (!string.IsNullOrWhiteSpace(search)) q["search"] = search;
        if (from is not null) q["from"] = from.Value.ToString("O");
        if (to is not null) q["to"] = to.Value.ToString("O");
        if (!string.IsNullOrWhiteSpace(path)) q["path"] = path;

        var url = $"api/logs/paged?{q}"; // ← ОТНОСИТЕЛЬНЫЙ путь
        return await _http.GetFromJsonAsync<PagedResult<LogEntry>>(url, ct)
               ?? new PagedResult<LogEntry>(Array.Empty<LogEntry>(), 0);
    }

    // Экспорт CSV: GET /api/logs/export?path=..
    public Task<byte[]> ExportLogsCsvAsync(string? path = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(path) ? "api/logs/export" : $"api/logs/export?path={Uri.EscapeDataString(path)}";
        return _http.GetByteArrayAsync(url, ct);
    }

    // Очистка активного лога: DELETE /api/logs?path=..
    public async Task ClearLogAsync(string? path = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(path) ? "api/logs" : $"api/logs?path={Uri.EscapeDataString(path)}";
        var resp = await _http.DeleteAsync(url, ct);
        resp.EnsureSuccessStatusCode();
    }
}
