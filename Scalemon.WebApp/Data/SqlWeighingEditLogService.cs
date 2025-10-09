using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Scalemon.WebApp.Models;

namespace Scalemon.WebApp.Data;

public sealed class SqlWeighingEditLogService : IWeighingEditLogService
{
    private const string AuditTable = "[dbo].[WeighingEdits]";

    private readonly ISettingsSource _settings;
    private string? _connString;

    public SqlWeighingEditLogService(ISettingsSource settings)
    {
        _settings = settings;
    }

    private async Task EnsureConfiguredAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
        {
            var dto = await _settings.LoadAsync(ct);
            _connString = dto?.DatabaseSettings?.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(_connString))
        {
            throw new InvalidOperationException("DatabaseSettings.ConnectionString не задан. Откройте «Настройки» и сохраните параметры БД.");
        }
    }

    private SqlConnection NewConn() => new(_connString);

    public async Task<IReadOnlyList<WeighingEditEntry>> GetAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? editedBy = null,
        WeighingEditAction? action = null,
        CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        var sql = new StringBuilder($@"SELECT [Id],[WeighingId],[EditedAt],[EditedBy],[Action],[OldWeight],[NewWeight],[RecordedAtSnapshot],[Comment] FROM {AuditTable} WHERE 1 = 1");
        var parameters = new List<SqlParameter>();

        if (from.HasValue)
        {
            sql.Append(" AND [EditedAt] >= @from");
            parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Value = from.Value });
        }
        if (to.HasValue)
        {
            sql.Append(" AND [EditedAt] <= @to");
            parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Value = to.Value });
        }
        if (!string.IsNullOrWhiteSpace(editedBy))
        {
            sql.Append(" AND [EditedBy] = @user");
            parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, 256) { Value = editedBy });
        }
        if (action.HasValue && action.Value != WeighingEditAction.Unknown)
        {
            sql.Append(" AND [Action] = @action");
            parameters.Add(new SqlParameter("@action", SqlDbType.NVarChar, 32) { Value = action.Value.ToString() });
        }

        sql.Append(" ORDER BY [EditedAt] DESC;");

        var result = new List<WeighingEditEntry>();
        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql.ToString(), conn);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.Add(parameter);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var actionStr = reader.IsDBNull(4) ? null : reader.GetString(4);
            var parsed = Enum.TryParse<WeighingEditAction>(actionStr, true, out var value)
                ? value
                : WeighingEditAction.Unknown;

            result.Add(new WeighingEditEntry(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                parsed,
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetEditorsAsync(CancellationToken ct = default)
    {
        await EnsureConfiguredAsync(ct);

        var sql = $@"SELECT DISTINCT LTRIM(RTRIM([EditedBy])) AS [EditedBy]
FROM {AuditTable}
WHERE [EditedBy] IS NOT NULL AND LTRIM(RTRIM([EditedBy])) <> ''
ORDER BY [EditedBy];";

        var result = new List<string>();
        await using var conn = NewConn();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
