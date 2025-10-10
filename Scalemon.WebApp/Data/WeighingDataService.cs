using Microsoft.Extensions.Configuration;
using Scalemon.WebApp.Models;
using System.Collections.Concurrent;
using System.Threading;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data
{
    /// <summary>
    /// In-memory генератор/хранилище взвешиваний для демо-UI.
    /// При первом запросе на день данные генерируются и кэшируются.
    /// </summary>
    public sealed class WeighingDataService : IWeighingDataService, IWeighingEditLogService
    {
        private readonly IConfiguration _cfg; // не используется в заглушке, но оставлен для совместимости DI
        public WeighingDataService(IConfiguration cfg) => _cfg = cfg;

        private static readonly object _sync = new();
        private static int _nextId = 1;
        private static int _nextEditId = 0;

        // Данные по дням
        private static readonly ConcurrentDictionary<DateOnly, List<Weighing>> _data = new();
        private static readonly ConcurrentDictionary<DateOnly, List<WeighingEditEntry>> _edits = new();

        // Чтобы месячная статистика была стабильной, помним какие дни уже «заполнили»
        private static readonly HashSet<(int year, int month)> _monthsInitialized = new();

        // Один общий RNG на процесс (достаточно для заглушки)
        private static readonly Random _rng = new(12345);

        // Сколько записей генерировать на день (можете поменять)
        private const int MinPerDay = 600;   // минимум
        private const int MaxPerDay = 1200;  // максимум

        public Task<Dictionary<DateOnly, int>> GetMonthStatsAsync(int year, int month, CancellationToken ct = default)
        {
            InitializeMonthIfNeeded(year, month);

            var result = _data
                .Where(kv => kv.Key.Year == year && kv.Key.Month == month)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Count);

            return Task.FromResult(result);
        }

        public Task<(IReadOnlyList<Weighing> Items, int TotalCount)> GetDayPageAsync(
            DateOnly date, int pageIndex, int pageSize = 400, CancellationToken ct = default)
        {
            var list = EnsureDay(date);
            var total = list.Count;

            var skip = Math.Max(pageIndex, 0) * Math.Max(pageSize, 1);
            var page = list.Skip(skip).Take(pageSize).ToList();

            return Task.FromResult(((IReadOnlyList<Weighing>)page, total));
        }

        public Task AdjustAsync(int id, decimal delta, CancellationToken ct = default)
            => AdjustAsync(id, delta, null, ct);

        public Task AdjustAsync(int id, decimal delta, string? userName, CancellationToken ct = default)
        {
            lock (_sync)
            {
                var (day, idx) = FindById(id);
                if (idx >= 0)
                {
                    var w = _data[day][idx];
                    var newWeight = Round2(w.Weight + delta);
                    _data[day][idx] = w with { Weight = newWeight };

                    AddAuditEntry(day, new WeighingEditEntry(
                        NextEditId(),
                        w.Id,
                        DateTime.Now,
                        userName,
                        delta >= 0 ? WeighingEditAction.Increment : WeighingEditAction.Decrement,
                        w.Weight,
                        newWeight,
                        w.Timestamp,
                        null));
                }
            }
            return Task.CompletedTask;
        }

        public Task<int> InsertAboveAsync(int refId, decimal weight, CancellationToken ct = default)
            => InsertAboveAsync(refId, weight, null, ct);

        public Task<int> InsertAboveAsync(int refId, decimal weight, string? userName, CancellationToken ct = default)
            => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(-500), userName, null);

        public Task<int> InsertAboveAsync(int refId, decimal weight, DateTime timestamp, CancellationToken ct = default)
            => InsertAboveAsync(refId, weight, timestamp, null, ct);

        public Task<int> InsertAboveAsync(int refId, decimal weight, DateTime timestamp, string? userName, CancellationToken ct = default)
            => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(-500), userName, timestamp);

        public Task<int> InsertBelowAsync(int refId, decimal weight, CancellationToken ct = default)
            => InsertBelowAsync(refId, weight, null, ct);

        public Task<int> InsertBelowAsync(int refId, decimal weight, string? userName, CancellationToken ct = default)
            => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(500), userName, null);

        public Task<int> InsertBelowAsync(int refId, decimal weight, DateTime timestamp, CancellationToken ct = default)
            => InsertBelowAsync(refId, weight, timestamp, null, ct);

        public Task<int> InsertBelowAsync(int refId, decimal weight, DateTime timestamp, string? userName, CancellationToken ct = default)
            => InsertNearAsync(refId, weight, TimeSpan.FromMilliseconds(500), userName, timestamp);

        public Task DeleteAsync(int id, CancellationToken ct = default)
            => DeleteAsync(id, null, ct);

        public Task DeleteAsync(int id, string? userName, CancellationToken ct = default)
        {
            lock (_sync)
            {
                var (day, idx) = FindById(id);
                if (idx >= 0)
                {
                    var item = _data[day][idx];
                    _data[day].RemoveAt(idx);

                    AddAuditEntry(day, new WeighingEditEntry(
                        NextEditId(),
                        item.Id,
                        DateTime.Now,
                        userName,
                        WeighingEditAction.Delete,
                        item.Weight,
                        null,
                        item.Timestamp,
                        null));
                }
            }
            return Task.CompletedTask;
        }

        private Task<int> InsertNearAsync(int refId, decimal weight, TimeSpan offset, string? userName, DateTime? explicitTimestamp)
        {
            int newId = 0;
            lock (_sync)
            {
                var (day, idx) = FindById(refId);
                if (idx < 0)
                {
                    return Task.FromResult(0);
                }

                var refItem = _data[day][idx];
                var ts = explicitTimestamp ?? refItem.Timestamp.Add(offset);

                newId = _nextId++;
                var rounded = Round2(weight);
                var item = new Weighing(newId, rounded, ts);
                var insertIndex = offset < TimeSpan.Zero ? idx : Math.Min(idx + 1, _data[day].Count);
                _data[day].Insert(insertIndex, item);
                _data[day].Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                AddAuditEntry(day, new WeighingEditEntry(
                    NextEditId(),
                    item.Id,
                    DateTime.Now,
                    userName,
                    WeighingEditAction.Insert,
                    null,
                    rounded,
                    ts,
                    null));
            }

            return Task.FromResult(newId);
        }

        // ---------- helpers ----------

        private static int NextEditId() => Interlocked.Increment(ref _nextEditId);

        private static void AddAuditEntry(DateOnly day, WeighingEditEntry entry)
        {
            var list = _edits.GetOrAdd(day, _ => new List<WeighingEditEntry>());
            list.Add(entry);
            list.Sort((a, b) => b.EditedAt.CompareTo(a.EditedAt));
        }

        private static IReadOnlyList<WeighingEditEntry> SnapshotDayEntries(DateOnly day, string? userName)
        {
            if (!_edits.TryGetValue(day, out var list))
            {
                return Array.Empty<WeighingEditEntry>();
            }

            IEnumerable<WeighingEditEntry> query = list;
            if (!string.IsNullOrWhiteSpace(userName))
            {
                query = query.Where(e => string.Equals(e.EditedBy, userName, StringComparison.OrdinalIgnoreCase));
            }

            return query.ToList();
        }

        private static decimal Round2(decimal x) =>
            Math.Round(x, 2, MidpointRounding.AwayFromZero);

        private static List<Weighing> EnsureDay(DateOnly day)
        {
            return _data.GetOrAdd(day, d =>
            {
                lock (_sync)
                {
                    // если вдруг параллельно добавили — берём существующий
                    if (_data.TryGetValue(d, out var existing)) return existing;

                    // создаём новый список
                    var list = GenerateDay(d);
                    _data[d] = list;
                    return list;
                }
            });
        }

        private static List<Weighing> GenerateDay(DateOnly day)
        {
            // Кол-во записей в диапазоне [MinPerDay..MaxPerDay)
            var count = _rng.Next(MinPerDay, MaxPerDay);

            var list = new List<Weighing>(count);
            var dt = day.ToDateTime(TimeOnly.MinValue);

            for (int i = 0; i < count; i++)
            {
                // равномерно по дню, но с небольшим случайным шагом
                dt = dt.AddSeconds(_rng.Next(1, 8));

                // вес 10.00..99.99
                var cents = _rng.Next(1000, 10000); // 1000..9999
                var weight = Round2(cents / 100m);

                var id = _nextId++;
                list.Add(new Weighing(id, weight, dt));
            }

            return list;
        }

        private static void InitializeMonthIfNeeded(int year, int month)
        {
            lock (_sync)
            {
                if (!_monthsInitialized.Add((year, month)))
                    return;

                var daysInMonth = DateTime.DaysInMonth(year, month);
                for (int d = 1; d <= daysInMonth; d++)
                {
                    // Пример: примерно на 60% дней будут данные
                    if (_rng.NextDouble() < 0.60)
                    {
                        var day = new DateOnly(year, month, d);
                        if (!_data.ContainsKey(day))
                            _data[day] = GenerateDay(day);
                    }
                }
            }
        }

        private static (DateOnly day, int index) FindById(int id)
        {
            foreach (var kv in _data)
            {
                var idx = kv.Value.FindIndex(w => w.Id == id);
                if (idx >= 0) return (kv.Key, idx);
            }
            return (default, -1);
        }

        public Task<IReadOnlyList<WeighingEditEntry>> GetEditsAsync(DateOnly date, CancellationToken ct = default)
        {
            IReadOnlyList<WeighingEditEntry> snapshot;
            lock (_sync)
            {
                snapshot = SnapshotDayEntries(date, null);
            }

            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<WeighingEditEntry>> GetEditsAsync(DateOnly date, string? userName, CancellationToken ct = default)
        {
            IReadOnlyList<WeighingEditEntry> snapshot;
            lock (_sync)
            {
                snapshot = SnapshotDayEntries(date, userName);
            }

            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<Weighing>> GetDayAllAsync(DateOnly date, CancellationToken ct = default)
        {
            if (_data.TryGetValue(date, out var list))
            {
                return Task.FromResult<IReadOnlyList<Weighing>>(list);
            }
            else
            {
                return Task.FromResult<IReadOnlyList<Weighing>>(Array.Empty<Weighing>());
            }
        }

        public Task<IReadOnlyList<WeighingEditEntry>> GetAsync(
            DateTime? from = null,
            DateTime? to = null,
            string? editedBy = null,
            WeighingEditAction? action = null,
            CancellationToken ct = default)
        {
            List<WeighingEditEntry> entries;
            lock (_sync)
            {
                entries = _edits.Values.SelectMany(x => x).ToList();
            }

            IEnumerable<WeighingEditEntry> query = entries;
            if (from.HasValue)
            {
                query = query.Where(e => e.EditedAt >= from.Value);
            }
            if (to.HasValue)
            {
                query = query.Where(e => e.EditedAt <= to.Value);
            }
            if (!string.IsNullOrWhiteSpace(editedBy))
            {
                query = query.Where(e => string.Equals(e.EditedBy, editedBy, StringComparison.OrdinalIgnoreCase));
            }
            if (action.HasValue && action.Value != WeighingEditAction.Unknown)
            {
                query = query.Where(e => e.Action == action.Value);
            }

            var result = query
                .OrderByDescending(e => e.EditedAt)
                .ToList();

            return Task.FromResult<IReadOnlyList<WeighingEditEntry>>(result);
        }

        public Task<IReadOnlyList<string>> GetEditorsAsync(CancellationToken ct = default)
        {
            List<string> names;
            lock (_sync)
            {
                names = _edits.Values
                    .SelectMany(x => x)
                    .Select(e => e.EditedBy)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return Task.FromResult<IReadOnlyList<string>>(names);
        }

    }
}