using static Scalemon.Common.Enums;

namespace Scalemon.Common;

/// <summary>Предоставляет веб-интерфейсу последнее состояние, отправленное на контроллер индикации.</summary>
public interface IProductionIndicatorState
{
    /// <summary>Показывает, было ли состояние уже определено службой.</summary>
    bool IsKnown { get; }

    /// <summary>Возвращает последнюю команду основной индикации.</summary>
    ArduinoSignalCode Current { get; }

    /// <summary>Сохраняет команду основной индикации.</summary>
    void Set(ArduinoSignalCode signal);

    /// <summary>Сбрасывает состояние при потере связи с контроллером.</summary>
    void Reset();
}

/// <summary>Потокобезопасно хранит последнее состояние производственной индикации.</summary>
public sealed class ProductionIndicatorState : IProductionIndicatorState
{
    private int _current = -1;

    /// <inheritdoc />
    public bool IsKnown => Volatile.Read(ref _current) >= 0;

    /// <inheritdoc />
    public ArduinoSignalCode Current
    {
        get
        {
            var value = Volatile.Read(ref _current);
            return value >= 0
                ? (ArduinoSignalCode)value
                : throw new InvalidOperationException("Состояние производственной индикации ещё не определено.");
        }
    }

    /// <inheritdoc />
    public void Set(ArduinoSignalCode signal)
        => Volatile.Write(ref _current, (int)signal);

    /// <inheritdoc />
    public void Reset()
        => Volatile.Write(ref _current, -1);
}
