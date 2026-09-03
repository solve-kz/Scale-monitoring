using System;
using Scalemon.WebApp.Models;
using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data
{
    public interface IWeighingDataService
    {
        // Дни месяца, где есть данные: вернуть словарь День->Количество
        Task<Dictionary<DateOnly, int>> GetMonthStatsAsync(int year, int month, CancellationToken ct = default);

        /// <summary>Возвращает статистику месяца с возможностью исключить санитарный забой.</summary>
        Task<Dictionary<DateOnly, int>> GetMonthStatsAsync(
            int year,
            int month,
            bool includeSanitary,
            CancellationToken ct = default);

        // Страница за день (по 400 записей)
        Task<(IReadOnlyList<Weighing> Items, int TotalCount)> GetDayPageAsync(
            DateOnly date, int pageIndex, int pageSize = 400, CancellationToken ct = default);

        /// <summary>Возвращает страницу за день с возможностью исключить санитарный забой.</summary>
        Task<(IReadOnlyList<Weighing> Items, int TotalCount)> GetDayPageAsync(
            DateOnly date,
            int pageIndex,
            int pageSize,
            bool includeSanitary,
            CancellationToken ct = default);

        // Операции редактирования
        Task AdjustAsync(int id, decimal delta, CancellationToken ct = default);
        Task AdjustAsync(int id, decimal delta, string? userName, CancellationToken ct = default);
        Task<int> InsertAboveAsync(int refId, decimal weight, CancellationToken ct = default);
        Task<int> InsertAboveAsync(int refId, decimal weight, string? userName, CancellationToken ct = default);
        Task<int> InsertAboveAsync(int refId, decimal weight, DateTime timestamp, CancellationToken ct = default);
        Task<int> InsertAboveAsync(int refId, decimal weight, DateTime timestamp, string? userName, CancellationToken ct = default);
        Task<int> InsertBelowAsync(int refId, decimal weight, CancellationToken ct = default);
        Task<int> InsertBelowAsync(int refId, decimal weight, string? userName, CancellationToken ct = default);
        Task<int> InsertBelowAsync(int refId, decimal weight, DateTime timestamp, CancellationToken ct = default);
        Task<int> InsertBelowAsync(int refId, decimal weight, DateTime timestamp, string? userName, CancellationToken ct = default);
        Task DeleteAsync(int id, CancellationToken ct = default);
        Task DeleteAsync(int id, string? userName, CancellationToken ct = default);

        Task<IReadOnlyList<Weighing>> GetDayAllAsync(DateOnly date, CancellationToken ct = default);

        /// <summary>Возвращает все записи за день с возможностью исключить санитарный забой.</summary>
        Task<IReadOnlyList<Weighing>> GetDayAllAsync(
            DateOnly date,
            bool includeSanitary,
            CancellationToken ct = default);

        /// <summary>Возвращает счётчик и последнее взвешивание для крупного экрана.</summary>
        Task<DayLiveSnapshot> GetDaySnapshotAsync(
            DateOnly date,
            bool includeSanitary = true,
            CancellationToken ct = default);

        Task<IReadOnlyList<WeighingEditEntry>> GetEditsAsync(DateOnly date, CancellationToken ct = default);
        Task<IReadOnlyList<WeighingEditEntry>> GetEditsAsync(DateOnly date, string? userName, CancellationToken ct = default);
    }
}
