using System.Data;
using Microsoft.Data.SqlClient;
using Scalemon.WebApp.Models;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data;

/// <summary>
/// Реализация <see cref="IWeighingDataService"/> поверх MS SQL (Express).
/// Читает настройки из секции DatabaseSettings и автоматически их перечитывает при изменении.
/// </summary>
public sealed class SqlWeighingDataService : IWeighingDataService
{
    private const string AuditTable = "[dbo].[WeighingEdits]";

    private readonly ISettingsSource _settings;
    private string? _connString;
    private string _tableName = "Weighings";

    public SqlWeighingDataService(ISettingsSource settings)
    {
        _settings = settings;
    }

    private async Task LoadDbSettingsAsync(CancellationToken ct)
    {
        var dto = await _settings.LoadAsync(ct);
        var db = dto?.DatabaseSettings;
        _connString = db?.ConnectionString;
        _tableName = string.IsNullOrWhiteSpace(db?.TableName) ? "Weighings" : db!.TableName;
    }

    private bool HasConn => !string.IsNullOrWhiteSpace(_connString);
    private SqlConnection NewConn() => new(_connString);

    private string QTable()
    {
        var parts = _tableName.Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? $"[{parts[0]}].[{parts[1]}]" : $"[{parts[0]}]";
    }

    private async Task EnsureConfiguredAsync(CancellationToken ct)
    {
        if (!HasConn)
        {
            await LoadDbSettingsAsync(ct);
            if (!HasConn)
            {
                throw new InvalidOperationException("DatabaseSettings.ConnectionString не задан. Откройте «Настройки» и сохраните параметры БД.");
            }
        }
    }

    private static decimal Round2(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static SqlParameter CreateDecimal(string name, decimal? value)
    {
        var parameter = new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 18,
            Scale = 2,
            Value = value is null ? DBNull.Value : value
        };
        return parameter;
    }

    private static SqlParameter CreateDate(string name, DateTime? value)
    {
        var parameter = new SqlParameter(name, SqlDbType.DateTime2)
        {
            Value = value ?? (object)DBNull.Value
        };
        return parameter;
    }

    private static SqlParameter CreateString(string name, string? value, int size)
    {
        var parameter = new SqlParameter(name, SqlDbType.NVarChar, size)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        };
        return parameter;
    }

    private async Task LogEditAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int weighingId,
        DateTime? recordedAt,
        WeighingEditAction action,
        decimal? oldWeight,
        decimal? newWeight,
        string? editedBy,
        string? comment,
        CancellationToken ct)
    {
        var sql = $@"
INSERT INTO {AuditTable} ([WeighingId],[EditedAt],[EditedBy],[Action],[OldWeight],[NewWeight],[RecordedAtSnapshot],[Comment])
VALUES (@id,@editedAt,@editedBy,@action,@oldWeight,@newWeight,@recordedAt,@comment);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = weighingId });
        cmd.Parameters.Add(new SqlParameter("@editedAt", SqlDbType.DateTime2) { Value = DateTime.Now });
        cmd.Parameters.Add(CreateString("@editedBy", editedBy, 256));
        cmd.Parameters.Add(CreateString("@action", action.ToString(), 32));
        cmd.Parameters.Add(CreateDecimal("@oldWeight", oldWeight));
        cmd.Parameters.Add(CreateDecimal("@newWeight", newWeight));
        cmd.Parameters.Add(CreateDate("@recordedAt", recordedAt));
        cmd.Parameters.Add(CreateString("@comment", comment, 512));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<DateOnly, int>> GetMonthStatsAsync(int year, int month, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        var s = new DateTime(year, month, 1);
        var e = s.AddMonths(1);

        var sql = $@"
SELECT CAST([RecordedAt] AS date) AS [Day], COUNT(*) AS [C]
FROM {QTable()}
WHERE [RecordedAt] >= @s AND [RecordedAt] < @e
GROUP BY CAST([RecordedAt] AS date);";

        var dict = new Dictionary<DateOnly, int>();
        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.DateTime2) { Value = s });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.DateTime2) { Value = e });

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            dict[DateOnly.FromDateTime(reader.GetDateTime(0))] = reader.GetInt32(1);
        }
        return dict;
    }

    public async Task<(IReadOnlyList<Weighing> Items, int TotalCount)> GetDayPageAsync(
        DateOnly day,
        int pageIndex,
        int pageSize = 400,
        CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        var s = day.ToDateTime(TimeOnly.MinValue);
        var e = day.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var skip = pageIndex * pageSize;
        var take = pageSize;

        var sqlCount = $@"SELECT COUNT(*) FROM {QTable()} WHERE [RecordedAt] >= @s AND [RecordedAt] < @e;";
        var sqlData = $@"
SELECT [Id],[RecordedAt],[Weight]
FROM {QTable()}
WHERE [RecordedAt] >= @s AND [RecordedAt] < @e
ORDER BY [RecordedAt] ASC
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;";

        var items = new List<Weighing>();
        int total;

        await using var conn = NewConn();
        await conn.OpenAsync(ct);

        await using (var c = new SqlCommand(sqlCount, conn))
        {
            c.Parameters.Add(new SqlParameter("@s", SqlDbType.DateTime2) { Value = s });
            c.Parameters.Add(new SqlParameter("@e", SqlDbType.DateTime2) { Value = e });
            total = Convert.ToInt32(await c.ExecuteScalarAsync(ct) ?? 0);
        }

        await using (var cmd = new SqlCommand(sqlData, conn))
        {
            cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.DateTime2) { Value = s });
            cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.DateTime2) { Value = e });
            cmd.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = skip });
            cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var ts = reader.GetDateTime(1);
                var w = reader.GetDecimal(2);
                items.Add(new Weighing(id, w, ts));
            }
        }

        return (items, total);
    }

    public Task AdjustAsync(int id, decimal delta, CancellationToken ct = default)
        => AdjustAsync(id, delta, null, ct);

    public async Task AdjustAsync(int id, decimal delta, string? userName, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            var selectSql = $"SELECT [Weight],[RecordedAt] FROM {QTable()} WHERE [Id]=@id;";
            decimal? oldWeight = null;
            DateTime? recordedAt = null;

            await using (var selectCmd = new SqlCommand(selectSql, conn, tx))
            {
                selectCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                using var reader = await selectCmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                if (await reader.ReadAsync(ct))
                {
                    oldWeight = reader.GetDecimal(0);
                    recordedAt = reader.GetDateTime(1);
                }
            }

            if (oldWeight is null)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            var newWeight = Round2(oldWeight.Value + delta);

            var updateSql = $"UPDATE {QTable()} SET [Weight] = @weight WHERE [Id]=@id;";
            await using (var updateCmd = new SqlCommand(updateSql, conn, tx))
            {
                updateCmd.Parameters.Add(CreateDecimal("@weight", newWeight));
                updateCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            var action = delta >= 0 ? WeighingEditAction.Increment : WeighingEditAction.Decrement;
            await LogEditAsync(conn, tx, id, recordedAt, action, oldWeight, newWeight, userName, null, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task<int> InsertAboveAsync(int refId, decimal weight, CancellationToken ct = default)
        => InsertAboveAsync(refId, weight, null, ct);

    public Task<int> InsertAboveAsync(int refId, decimal weight, string? userName, CancellationToken ct = default)
        => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(-500), userName, ct);

    public Task<int> InsertBelowAsync(int refId, decimal weight, CancellationToken ct = default)
        => InsertBelowAsync(refId, weight, null, ct);

    public Task<int> InsertBelowAsync(int refId, decimal weight, string? userName, CancellationToken ct = default)
        => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(500), userName, ct);

    private async Task<int> InsertNearAsync(int refId, decimal weight, TimeSpan offset, string? userName, CancellationToken ct)
    {
        await EnsureConfiguredAsync(ct);

        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            var getTsSql = $"SELECT [RecordedAt] FROM {QTable()} WHERE [Id]=@id;";
            DateTime? baseTs = null;
            await using (var get = new SqlCommand(getTsSql, conn, tx))
            {
                get.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = refId });
                var scalar = await get.ExecuteScalarAsync(ct);
                baseTs = scalar as DateTime?;
            }

            if (baseTs is null)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            var ts = baseTs.Value + offset;
            var roundedWeight = Round2(weight);

            var insSql = $@"
INSERT INTO {QTable()} ([Weight],[RecordedAt]) VALUES (@w,@ts);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            int newId;
            await using (var ins = new SqlCommand(insSql, conn, tx))
            {
                ins.Parameters.Add(CreateDecimal("@w", roundedWeight));
                ins.Parameters.Add(new SqlParameter("@ts", SqlDbType.DateTime2) { Value = ts });
                newId = Convert.ToInt32(await ins.ExecuteScalarAsync(ct) ?? 0);
            }

            if (newId > 0)
            {
                await LogEditAsync(conn, tx, newId, ts, WeighingEditAction.Insert, null, roundedWeight, userName, null, ct);
            }

            await tx.CommitAsync(ct);
            return newId;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
        => DeleteAsync(id, null, ct);

    public async Task DeleteAsync(int id, string? userName, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            var selectSql = $"SELECT [Weight],[RecordedAt] FROM {QTable()} WHERE [Id]=@id;";
            decimal? weight = null;
            DateTime? recordedAt = null;

            await using (var select = new SqlCommand(selectSql, conn, tx))
            {
                select.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                using var reader = await select.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                if (await reader.ReadAsync(ct))
                {
                    weight = reader.GetDecimal(0);
                    recordedAt = reader.GetDateTime(1);
                }
            }

            if (weight is null)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            var deleteSql = $"DELETE FROM {QTable()} WHERE [Id]=@id;";
            await using (var deleteCmd = new SqlCommand(deleteSql, conn, tx))
            {
                deleteCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            await LogEditAsync(conn, tx, id, recordedAt, WeighingEditAction.Delete, weight, null, userName, null, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<Weighing>> GetDayAllAsync(DateOnly date, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        var s = date.ToDateTime(TimeOnly.MinValue);
        var e = date.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var sql = $@"
SELECT [Id], [Weight], [RecordedAt]
FROM {QTable()}
WHERE [RecordedAt] >= @s AND [RecordedAt] < @e
ORDER BY [RecordedAt];";

        var list = new List<Weighing>();

        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.DateTime2) { Value = s });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.DateTime2) { Value = e });

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Weighing(
                reader.GetInt32(0),
                reader.GetDecimal(1),
                reader.GetDateTime(2)));
        }

        return list;
    }

    public Task<IReadOnlyList<WeighingEditEntry>> GetEditsAsync(DateOnly date, CancellationToken ct = default)
        => GetEditsAsync(date, null, ct);

    public async Task<IReadOnlyList<WeighingEditEntry>> GetEditsAsync(DateOnly date, string? userName, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var filterByUser = !string.IsNullOrWhiteSpace(userName);

        var sql = $@"
SELECT [Id],[WeighingId],[EditedAt],[EditedBy],[Action],[OldWeight],[NewWeight],[RecordedAtSnapshot],[Comment]
FROM {AuditTable}
WHERE (([RecordedAtSnapshot] >= @s AND [RecordedAtSnapshot] < @e)
    OR ([RecordedAtSnapshot] IS NULL AND [EditedAt] >= @s AND [EditedAt] < @e))" +
            (filterByUser ? " AND [EditedBy] = @user" : string.Empty) +
            " ORDER BY [EditedAt] DESC;";

        var result = new List<WeighingEditEntry>();
        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.DateTime2) { Value = start });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.DateTime2) { Value = end });
        if (filterByUser)
        {
            cmd.Parameters.Add(CreateString("@user", userName, 256));
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var actionStr = reader.IsDBNull(4) ? null : reader.GetString(4);
            var action = Enum.TryParse<WeighingEditAction>(actionStr, ignoreCase: true, out var parsed)
                ? parsed
                : WeighingEditAction.Unknown;

            result.Add(new WeighingEditEntry(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                action,
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }
}
