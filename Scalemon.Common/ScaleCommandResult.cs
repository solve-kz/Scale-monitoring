namespace Scalemon.Common;

/// <summary>
/// Результат одной команды управления весовым терминалом.
/// </summary>
public sealed record ScaleCommandResult(
    bool Succeeded,
    long ResponseCode,
    string ResponseText,
    bool IsTransportError = false)
{
    /// <summary>
    /// Создаёт успешный результат команды.
    /// </summary>
    public static ScaleCommandResult Success(long responseCode, string responseText) =>
        new(true, responseCode, responseText);

    /// <summary>
    /// Создаёт результат явного отказа терминала.
    /// </summary>
    public static ScaleCommandResult Rejected(long responseCode, string responseText) =>
        new(false, responseCode, responseText);

    /// <summary>
    /// Создаёт результат ошибки транспорта или тайм-аута.
    /// </summary>
    public static ScaleCommandResult TransportFailure(string responseText) =>
        new(false, 1, responseText, true);
}

/// <summary>
/// Команда аппаратной коррекции остаточного веса.
/// </summary>
public enum ResidualCorrectionCommand
{
    SetZero,
    SetTare,
    TareCurrentWeight
}

/// <summary>
/// Описывает одну команду коррекции, запрошенную конечным автоматом.
/// </summary>
public sealed record ResidualCorrectionRequest(
    ResidualCorrectionCommand Command,
    decimal WeightKg = 0m);
