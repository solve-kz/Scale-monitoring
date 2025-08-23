using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Primitives;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data;

/// <summary>
/// Реализация IWeighingDataService поверх MS SQL (Express).
/// Читает настройки из секции DatabaseSettings и автоматически их перечитывает при изменении.
/// </summary>
public sealed class SqlWeighingDataService : IWeighingDataService


{
    private readonly ISettingsSource _settings; // <-- добавили
    private string? _connString;
    private string _tableName = "Weighings";

    public SqlWeighingDataService(ISettingsSource settings /*, ILogger<...> logger? */)
    {
        _settings = settings;
    }

    // Загружаем актуальные значения из того же источника, что и Settings.razor
    private async Task LoadDbSettingsAsync(CancellationToken ct)
    {
        var dto = await _settings.LoadAsync(ct);   // SettingsDto
        var db = dto?.DatabaseSettings;
        _connString = db?.ConnectionString;
        _tableName = string.IsNullOrWhiteSpace(db?.TableName) ? "Weighings" : db!.TableName;
    }

    private bool HasConn => !string.IsNullOrWhiteSpace(_connString);
    private SqlConnection NewConn() => new SqlConnection(_connString);

    // ------ ЧТЕНИЕ ДАННЫХ ------

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
                throw new InvalidOperationException("DatabaseSettings.ConnectionString не задан. Откройте «Настройки» и сохраните параметры БД.");
        }
    }
    // --- Календарь (метки по дням) ---
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
        cmd.Parameters.Add("@s", System.Data.SqlDbType.DateTime2).Value = s;
        cmd.Parameters.Add("@e", System.Data.SqlDbType.DateTime2).Value = e;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            dict[DateOnly.FromDateTime(r.GetDateTime(0))] = r.GetInt32(1);
        }
        return dict;
    }

    public async Task<(IReadOnlyList<Weighing> Items, int TotalCount)>
    GetDayPageAsync(DateOnly day, int pageIndex, int pageSize = 400, CancellationToken ct = default)
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
            c.Parameters.Add("@s", SqlDbType.DateTime2).Value = s;
            c.Parameters.Add("@e", SqlDbType.DateTime2).Value = e;
            total = Convert.ToInt32(await c.ExecuteScalarAsync(ct) ?? 0);
        }

        await using (var cmd = new SqlCommand(sqlData, conn))
        {
            cmd.Parameters.Add("@s", SqlDbType.DateTime2).Value = s;
            cmd.Parameters.Add("@e", SqlDbType.DateTime2).Value = e;
            cmd.Parameters.Add("@skip", SqlDbType.Int).Value = skip;
            cmd.Parameters.Add("@take", SqlDbType.Int).Value = take;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                var ts = r.GetDateTime(1);
                var w = r.GetDecimal(2);
                items.Add(new Weighing(id, w, ts));
            }
        }

        return (items, total);
    }

    // ------ ИЗМЕНЕНИЯ (контекстное меню в WeightsGrid) ------
    // --- Изменения (inc/dec/insert/delete) — без изменений по сути, только EnsureConfiguredAsync(ct) перед работой ---
    public async Task AdjustAsync(int id, decimal delta, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);
        var sql = $@"UPDATE {QTable()} SET [Weight] = CAST(ROUND([Weight] + @delta, 2) AS DECIMAL(18,2)) WHERE [Id]=@id;";
        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@delta", System.Data.SqlDbType.Decimal).Value = delta;
        cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = id;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<int> InsertAboveAsync(int refId, decimal weight, CancellationToken ct = default)
        => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(-500), ct);
    public Task<int> InsertBelowAsync(int refId, decimal weight, CancellationToken ct = default)
        => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(500), ct);

    private async Task<int> InsertNearAsync(int refId, decimal weight, TimeSpan offset, CancellationToken ct)
    {
        await EnsureConfiguredAsync(ct);

        var getTsSql = $@"SELECT [RecordedAt] FROM {QTable()} WHERE [Id]=@id;";
        var insSql = $@"
INSERT INTO {QTable()} ([Weight],[RecordedAt]) VALUES (@w,@ts);
SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var conn = NewConn();
        await conn.OpenAsync(ct);

        DateTime? baseTs;
        await using (var get = new SqlCommand(getTsSql, conn))
        {
            get.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = refId;
            baseTs = await get.ExecuteScalarAsync(ct) as DateTime?;
        }
        if (baseTs is null) return 0;

        var ts = baseTs.Value + offset;

        await using var ins = new SqlCommand(insSql, conn);
        ins.Parameters.Add("@w", System.Data.SqlDbType.Decimal).Value = Math.Round(weight, 2, MidpointRounding.AwayFromZero);
        ins.Parameters.Add("@ts", System.Data.SqlDbType.DateTime2).Value = ts;
        return Convert.ToInt32(await ins.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);
        var sql = $@"DELETE FROM {QTable()} WHERE [Id]=@id;";
        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = id;
        await cmd.ExecuteNonQueryAsync(ct);
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
        cmd.Parameters.Add("@s", System.Data.SqlDbType.DateTime2).Value = s;
        cmd.Parameters.Add("@e", System.Data.SqlDbType.DateTime2).Value = e;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Weighing(
                r.GetInt32(0),     // Id
                r.GetDecimal(1),   // Weight
                r.GetDateTime(2)   // RecordedAt
            ));
        }

        return list;
    }



    // ------ helpers ------

    void EnsureConfigured()
    {
        if (!HasConn)
            throw new InvalidOperationException(
                "DatabaseSettings:ConnectionString не задан. Откройте страницу Settings, заполните подключение и сохраните настройки.");
    }
}
