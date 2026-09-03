using Microsoft.Data.Sqlite;

namespace Scalemon.Common;

/// <summary>
/// Определяет производственный режим, в котором выполнено взвешивание.
/// </summary>
public enum SlaughterMode
{
    /// <summary>Обычный производственный забой.</summary>
    General = 0,

    /// <summary>Санитарный забой.</summary>
    Sanitary = 1
}

/// <summary>
/// Предоставляет текущее состояние двухпозиционного переключателя режима.
/// </summary>
public interface ISlaughterModeState
{
    /// <summary>Показывает, подтверждено ли положение переключателя контроллером.</summary>
    bool IsKnown { get; }

    /// <summary>Возвращает текущий режим.</summary>
    SlaughterMode Current { get; }

    /// <summary>Обновляет режим после сообщения контроллера.</summary>
    void Set(SlaughterMode mode);

    /// <summary>Сбрасывает подтверждение при потере связи с контроллером.</summary>
    void Reset();

    /// <summary>Ожидает первое подтверждённое положение переключателя.</summary>
    Task<SlaughterMode> WaitUntilKnownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Потокобезопасно хранит последнее состояние переключателя режима.
/// </summary>
public sealed class SlaughterModeState : ISlaughterModeState
{
    private readonly object _gate = new();
    private int _current = -1;
    private TaskCompletionSource<SlaughterMode> _known = CreateCompletionSource();

    /// <inheritdoc />
    public bool IsKnown => Volatile.Read(ref _current) >= 0;

    /// <inheritdoc />
    public SlaughterMode Current
    {
        get
        {
            var value = Volatile.Read(ref _current);
            return value >= 0
                ? (SlaughterMode)value
                : throw new InvalidOperationException("Положение переключателя режима ещё не подтверждено Arduino.");
        }
    }

    /// <inheritdoc />
    public void Set(SlaughterMode mode)
    {
        if (!Enum.IsDefined(typeof(SlaughterMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим забоя.");
        }

        lock (_gate)
        {
            Volatile.Write(ref _current, (int)mode);
            _known.TrySetResult(mode);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            Volatile.Write(ref _current, -1);
            if (_known.Task.IsCompleted)
            {
                _known = CreateCompletionSource();
            }
        }
    }

    /// <inheritdoc />
    public Task<SlaughterMode> WaitUntilKnownAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _current >= 0
                ? Task.FromResult((SlaughterMode)_current)
                : _known.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource<SlaughterMode> CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Хранит режим взвешивания отдельно от производственной схемы SQL Server.
/// </summary>
public interface IWeighingModeStore
{
    /// <summary>Создаёт локальное хранилище при первом запуске.</summary>
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>Записывает режим для сохранённого идентификатора взвешивания.</summary>
    Task UpsertAsync(
        int weighingId,
        DateTime recordedAt,
        decimal weight,
        SlaughterMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Возвращает известные режимы для набора идентификаторов.</summary>
    Task<IReadOnlyDictionary<int, SlaughterMode>> GetModesAsync(
        IEnumerable<int> weighingIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Реализует локальный журнал режимов на SQLite без изменения Prod-схемы SQL Server.
/// </summary>
public sealed class SqliteWeighingModeStore : IWeighingModeStore
{
    private const int QueryBatchSize = 400;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Создаёт журнал режимов в указанном файле SQLite.
    /// </summary>
    public SqliteWeighingModeStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Путь к журналу режимов не задан.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        _databasePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <inheritdoc />
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS WeighingModes (
                    WeighingId INTEGER NOT NULL PRIMARY KEY,
                    RecordedAt TEXT NOT NULL,
                    Weight TEXT NOT NULL,
                    Mode INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_WeighingModes_RecordedAt
                    ON WeighingModes (RecordedAt);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        int weighingId,
        DateTime recordedAt,
        decimal weight,
        SlaughterMode mode,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WeighingModes (WeighingId, RecordedAt, Weight, Mode, UpdatedAt)
            VALUES ($id, $recordedAt, $weight, $mode, $updatedAt)
            ON CONFLICT(WeighingId) DO UPDATE SET
                RecordedAt = excluded.RecordedAt,
                Weight = excluded.Weight,
                Mode = excluded.Mode,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$id", weighingId);
        command.Parameters.AddWithValue("$recordedAt", recordedAt.ToString("O"));
        command.Parameters.AddWithValue("$weight", weight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$mode", (int)mode);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, SlaughterMode>> GetModesAsync(
        IEnumerable<int> weighingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(weighingIds);
        await EnsureInitializedAsync(cancellationToken);

        var ids = weighingIds.Where(id => id > 0).Distinct().ToArray();
        var result = new Dictionary<int, SlaughterMode>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in ids.Chunk(QueryBatchSize))
        {
            await using var command = connection.CreateCommand();
            var parameterNames = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameterNames[index] = $"$id{index}";
                command.Parameters.AddWithValue(parameterNames[index], batch[index]);
            }

            command.CommandText = $"SELECT WeighingId, Mode FROM WeighingModes WHERE WeighingId IN ({string.Join(',', parameterNames)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var modeValue = reader.GetInt32(1);
                if (Enum.IsDefined(typeof(SlaughterMode), modeValue))
                {
                    result[reader.GetInt32(0)] = (SlaughterMode)modeValue;
                }
            }
        }

        return result;
    }
}
