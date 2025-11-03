using Microsoft.Data.Sqlite;
using Scalemon.ApiService.Models;
using Scalemon.Common.Logging;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Scalemon.ApiService.Services;

public sealed class SqliteLogRepository
{
    private readonly DailyLogDatabaseProvider _databaseProvider;

    public SqliteLogRepository(DailyLogDatabaseProvider databaseProvider)
    {
        _databaseProvider = databaseProvider;
        _databaseProvider.EnsureCurrentDatabase();
    }

    private SqliteConnection CreateConnection(string path)
        => new(_databaseProvider.GetConnectionStringForPath(path));

    public async Task<IReadOnlyList<LogEntry>> GetRecentAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0) return Array.Empty<LogEntry>();

        var dbFiles = _databaseProvider.EnumerateDatabases();
        var aggregated = new List<LogEntry>(limit);

        foreach (var db in dbFiles)
        {
            var remaining = limit - aggregated.Count;
            if (remaining <= 0) break;

            await using var connection = CreateConnection(db.Path);
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT Timestamp, Level, Source, Message, Exception
FROM {LogDatabaseInitializer.TableName}
ORDER BY datetime(Timestamp) DESC
LIMIT $limit;";
            cmd.Parameters.Add(new SqliteParameter("$limit", remaining));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                aggregated.Add(ReadEntry(reader));
            }
        }

        aggregated.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));
        if (aggregated.Count > limit)
        {
            aggregated.RemoveRange(limit, aggregated.Count - limit);
        }

        return aggregated;
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

        var filterParameters = new List<(string Name, object? Value)>();
        var whereClause = BuildWhereClause(allowedLevels, search, from, to, filterParameters);

        var dbFiles = _databaseProvider.EnumerateDatabases(from, to);
        var items = new List<LogEntry>(take * Math.Max(1, dbFiles.Count));
        long total = 0;

        foreach (var db in dbFiles)
        {
            await using var connection = CreateConnection(db.Path);
            await connection.OpenAsync(ct);

            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM {LogDatabaseInitializer.TableName} {whereClause};";
                AddParameters(countCmd, filterParameters);
                var scalar = await countCmd.ExecuteScalarAsync(ct);
                total += scalar is long l ? l : Convert.ToInt64(scalar ?? 0);
            }

            await using (var dataCmd = connection.CreateCommand())
            {
                dataCmd.CommandText = $@"SELECT Timestamp, Level, Source, Message, Exception
FROM {LogDatabaseInitializer.TableName}
{whereClause}
ORDER BY datetime(Timestamp) ASC;";
                AddParameters(dataCmd, filterParameters);

                await using var reader = await dataCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(ReadEntry(reader));
                }
            }
        }

        items.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        var pageItems = items.Skip(skip).Take(take).ToList();

        return new PagedResult<LogEntry>(pageItems, (int)Math.Min(int.MaxValue, total));
    }

    public async Task<IReadOnlyList<LogEntry>> GetAllAsync(
        IReadOnlyList<string>? allowedLevels,
        string? search,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        var filterParameters = new List<(string Name, object? Value)>();
        var whereClause = BuildWhereClause(allowedLevels, search, from, to, filterParameters);

        var dbFiles = _databaseProvider.EnumerateDatabases(from, to);
        var items = new List<LogEntry>(1024 * Math.Max(1, dbFiles.Count));

        foreach (var db in dbFiles)
        {
            await using var connection = CreateConnection(db.Path);
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT Timestamp, Level, Source, Message, Exception
FROM {LogDatabaseInitializer.TableName}
{whereClause}
ORDER BY datetime(Timestamp) ASC;";
            AddParameters(cmd, filterParameters);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadEntry(reader));
            }
        }

        items.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return items;
    }

    public async Task AppendAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        var grouped = entries.GroupBy(e => e.Timestamp.Date);

        foreach (var group in grouped)
        {
            var connectionString = _databaseProvider.GetConnectionString(group.Key);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(ct);

            await using DbCommand cmd = connection.CreateCommand();
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

            foreach (var entry in group)
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
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        var dbFiles = _databaseProvider.EnumerateDatabases();

        foreach (var db in dbFiles)
        {
            await using var connection = CreateConnection(db.Path);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {LogDatabaseInitializer.TableName};";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<ImportSummary> ImportAsync(
        IAsyncEnumerable<LogEntry> entries,
        string originalFileName,
        string? requestedName,
        CancellationToken ct)
    {
        var path = _databaseProvider.GetImportPath(requestedName, originalFileName, out var databaseFileName);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to reset target database '{path}': {ex.Message}", ex);
        }

        LogDatabaseInitializer.EnsureDatabase(path);

        await using var connection = new SqliteConnection(_databaseProvider.GetConnectionStringForPath(path));
        await connection.OpenAsync(ct);
        LogDatabaseInitializer.EnsureSchema(connection);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct);
        await using DbCommand cmd = connection.CreateCommand();
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

        var inserted = 0;

        await foreach (var entry in entries.WithCancellation(ct))
        {
            tsParam.Value = ToIsoString(entry.Timestamp);
            levelParam.Value = entry.Level ?? string.Empty;
            sourceParam.Value = ToDbValue(entry.Source);
            messageParam.Value = ToDbValue(entry.Message);
            exceptionParam.Value = ToDbValue(entry.Exception);
            await cmd.ExecuteNonQueryAsync(ct);
            inserted++;
        }

        await transaction.CommitAsync(ct);

        return new ImportSummary(originalFileName, databaseFileName, path, inserted);
    }

    public readonly record struct ImportSummary(string SourceFile, string DatabaseFileName, string DatabasePath, int ImportedCount);

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
