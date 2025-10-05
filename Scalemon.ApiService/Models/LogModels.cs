namespace Scalemon.ApiService.Models;

public sealed record LogEntry(DateTime Timestamp, string Level, string Source, string Message, string? Exception);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);
