namespace Scalemon.WebApp.Models;

/// <summary>
/// Действие, зафиксированное в аудите взвешиваний.
/// </summary>
public enum WeighingEditAction
{
    Unknown = 0,
    Increment,
    Decrement,
    Insert,
    Delete,
    ManualUpdate
}

/// <summary>
/// Строка журнала правок записей о взвешиваниях.
/// </summary>
/// <param name="Id">Идентификатор строки аудита.</param>
/// <param name="WeighingId">Идентификатор записи взвешивания (если она ещё существует).</param>
/// <param name="EditedAt">Время фиксации изменения.</param>
/// <param name="EditedBy">Пользователь, выполнивший изменение.</param>
/// <param name="Action">Тип действия.</param>
/// <param name="OldWeight">Предыдущее значение веса.</param>
/// <param name="NewWeight">Новое значение веса.</param>
/// <param name="RecordedAtSnapshot">Время фиксации веса на момент изменения.</param>
/// <param name="Comment">Дополнительный комментарий.</param>
public sealed record WeighingEditEntry(
    int Id,
    int? WeighingId,
    DateTime EditedAt,
    string? EditedBy,
    WeighingEditAction Action,
    decimal? OldWeight,
    decimal? NewWeight,
    DateTime? RecordedAtSnapshot,
    string? Comment);
