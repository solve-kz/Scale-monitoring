using static Scalemon.WebApp.Models.WeighingModels;

namespace Scalemon.WebApp.Data
{
    public interface IWeighingDataService
    {
        // Дни месяца, где есть данные: вернуть словарь День->Количество
        Task<Dictionary<DateOnly, int>> GetMonthStatsAsync(int year, int month, CancellationToken ct = default);

        // Страница за день (по 400 записей)
        Task<(IReadOnlyList<Weighing> Items, int TotalCount)> GetDayPageAsync(
    DateOnly date, int pageIndex, int pageSize = 400, CancellationToken ct = default);

        // Операции редактирования
        Task AdjustAsync(int id, decimal delta, CancellationToken ct = default);
        Task<int> InsertAboveAsync(int refId, decimal weight, CancellationToken ct = default);
        Task<int> InsertBelowAsync(int refId, decimal weight, CancellationToken ct = default);
        Task DeleteAsync(int id, CancellationToken ct = default);

        Task<IReadOnlyList<Weighing>> GetDayAllAsync(DateOnly date, CancellationToken ct = default);
    }
}
