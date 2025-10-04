using Microsoft.Data.Sqlite;
using Scalemon.ApiService.Models;
using Scalemon.Common.Logging;

namespace Scalemon.ApiService.Services;

public sealed class SqliteLogRepository
{
    private readonly string _connectionString;

    public SqliteLogRepository(string databasePath)
    {
        LogDatabaseInitializer.EnsureDatabase(databasePath);
        _connectionString = LogDatabaseInitializer.BuildConnectionString(databasePath);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    public async Task<IReadOnlyList<LogEntry>> GetRecentAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0) return Array.Empty<LogEntry>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"SELECT Timestamp, Level, Source, Message, Exception
FROM {LogDatabaseInitializer.TableName}
ORDER BY datetime(Timestamp) DESC
LIMIT $limit;";
        cmd.Parameters.Add(new SqliteParameter("$limit", limit));

        var result = new List<LogEntry>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public async Task<PagedResult<LogEntry>> GetPagedAsync(
        int skip,
        int take,
        IReadOnlyList<string>? allowedLevels,
        string? search,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 5000);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var filterParameters = new List<(string Name, object? Value)>();
        var whereClause = BuildWhereClause(allowedLevels, search, from, to, filterParameters);

        long total = 0;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM {LogDatabaseInitializer.TableName} {whereClause};";
            AddParameters(countCmd, filterParameters);
            var scalar = await countCmd.ExecuteScalarAsync(ct);
            total = scalar is long l ? l : Convert.ToInt64(scalar ?? 0);
        }

        var items = new List<LogEntry>(take);
        await using (var dataCmd = connection.CreateCommand())
        {
            dataCmd.CommandText = $@"SELECT Timestamp, Level, Source, Message, Exception
FROM {LogDatabaseInitializer.TableName}
{whereClause}
ORDER BY datetime(Timestamp) DESC
LIMIT $take OFFSET $skip;";
            AddParameters(dataCmd, filterParameters);
            dataCmd.Parameters.Add(new SqliteParameter("$take", take));
            dataCmd.Parameters.Add(new SqliteParameter("$skip", skip));

            await using var reader = await dataCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadEntry(reader));
            }
        }

        return new PagedResult<LogEntry>(items, (int)Math.Min(int.MaxValue, total));
    }

    public async Task<IReadOnlyList<LogEntry>> GetAllAsync(
        IReadOnlyList<string>? allowedLevels,
        string? search,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var filterParameters = new List<(string Name, object? Value)>();
        var whereClause = BuildWhereClause(allowedLevels, search, from, to, filterParameters);

        var items = new List<LogEntry>(1024);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"SELECT Timestamp, Level, Source, Message, Exception
FROM {LogDatabaseInitializer.TableName}
{whereClause}
ORDER BY datetime(Timestamp) DESC;";
        AddParameters(cmd, filterParameters);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(ReadEntry(reader));
        }

        return items;
    }

    public async Task AppendAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"INSERT INTO {LogDatabaseInitializer.TableName}
(Timestamp, Level, Source, Message, Exception)
VALUES ($ts, $level, $source, $message, $exception);";

        var tsParam = cmd.CreateParameter();
        tsParam.ParameterName = "$ts";
        cmd.Parameters.Add(tsParam);

        var levelParam = cmd.CreateParameter();
        levelParam.ParameterName = "$level";
        cmd.Parameters.Add(levelParam);

        var sourceParam = cmd.CreateParameter();
        sourceParam.ParameterName = "$source";
        cmd.Parameters.Add(sourceParam);

        var messageParam = cmd.CreateParameter();
        messageParam.ParameterName = "$message";
        cmd.Parameters.Add(messageParam);

        var exceptionParam = cmd.CreateParameter();
        exceptionParam.ParameterName = "$exception";
        cmd.Parameters.Add(exceptionParam);

        foreach (var entry in entries)
        {
            tsParam.Value = ToIsoString(entry.Timestamp);
            levelParam.Value = entry.Level ?? string.Empty;
            sourceParam.Value = ToDbValue(entry.Source);
            messageParam.Value = ToDbValue(entry.Message);
            exceptionParam.Value = ToDbValue(entry.Exception);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {LogDatabaseInitializer.TableName};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqliteParameter(name, value ?? DBNull.Value));
        }
    }

    private static string BuildWhereClause(
        IReadOnlyList<string>? allowedLevels,
        string? search,
        DateTime? from,
        DateTime? to,
        List<(string Name, object? Value)> parameters)
    {
        var clauses = new List<string>();

        if (allowedLevels is { Count: > 0 })
        {
            var placeholders = new List<string>(allowedLevels.Count);
            for (var i = 0; i < allowedLevels.Count; i++)
            {
                var name = "$level" + i.ToString();
                placeholders.Add(name);
                parameters.Add((name, allowedLevels[i]));
            }
            clauses.Add($"Level IN ({string.Join(",", placeholders)})");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = "%" + search + "%";
            parameters.Add(("$search", term));
            clauses.Add("(Message LIKE $search OR Source LIKE $search OR Exception LIKE $search)");
        }

        if (from.HasValue)
        {
            parameters.Add(("$from", ToIsoString(from.Value)));
            clauses.Add("datetime(Timestamp) >= datetime($from)");
        }

        if (to.HasValue)
        {
            parameters.Add(("$to", ToIsoString(to.Value)));
            clauses.Add("datetime(Timestamp) <= datetime($to)");
        }

        return clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;
    }

    private static LogEntry ReadEntry(SqliteDataReader reader)
    {
        var tsText = reader.GetString(0);
        DateTime timestamp;
        if (DateTimeOffset.TryParse(tsText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
        {
            timestamp = dto.ToLocalTime().DateTime;
        }
        else if (DateTime.TryParse(tsText, out var dt))
        {
            timestamp = DateTime.SpecifyKind(dt, DateTimeKind.Local);
        }
        else
        {
            timestamp = DateTime.Now;
        }

        var level = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var source = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var message = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var exception = reader.IsDBNull(4) ? null : reader.GetString(4);

        return new LogEntry(timestamp, level, source, message, exception);
    }

    private static string ToIsoString(DateTime timestamp)
    {
        if (timestamp.Kind == DateTimeKind.Unspecified)
        {
            timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Local);
        }
        var dto = new DateTimeOffset(timestamp);
        return dto.ToUniversalTime().ToString("o");
    }

    private static object? ToDbValue(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;
}
