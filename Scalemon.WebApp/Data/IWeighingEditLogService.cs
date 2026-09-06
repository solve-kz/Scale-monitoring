using Scalemon.WebApp.Models;

namespace Scalemon.WebApp.Data;

public interface IWeighingEditLogService
{
    Task<IReadOnlyList<WeighingEditEntry>> GetAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? editedBy = null,
        WeighingEditAction? action = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetEditorsAsync(CancellationToken ct = default);
}
