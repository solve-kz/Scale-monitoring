''' <summary>
''' Определяет контракт для высокоуровневого процессора, который управляет циклом опроса весов
''' и предоставляет систему событий для остальной части приложения.
''' </summary>
Public Interface IScaleProcessor
    ''' <summary>
    ''' Запускает таймер опроса весов.
    ''' </summary>
    Sub Start()

    ''' <summary>
    ''' Останавливает таймер опроса весов.
    ''' </summary>
    Sub [Stop]()

    ''' <summary>
    ''' Асинхронно отправляет команду сброса веса на ноль.
    ''' </summary>
    Function ResetToZeroAsync() As Task

    ''' <summary>
    ''' Подписывает обработчик на событие получения нового стабильного веса.
    ''' </summary>
    Sub SubscribeWeightReceived(handler As Func(Of Decimal, Task))

    ''' <summary>
    ''' Отписывает обработчик от события получения нового стабильного веса.
    ''' </summary>
    Sub UnsubscribeWeightReceived(handler As Func(Of Decimal, Task))

    ''' <summary>
    ''' Подписывает обработчик на событие, когда вес становится нестабильным.
    ''' </summary>
    Sub SubscribeUnstable(handler As Func(Of Task))

    ''' <summary>
    ''' Отписывает обработчик от события, когда вес становится нестабильным.
    ''' </summary>
    Sub UnsubscribeUnstable(handler As Func(Of Task))

    ''' <summary>
    ''' Подписывает обработчик на событие потери связи с весами.
    ''' </summary>
    Sub SubscribeConnectionLost(handler As Func(Of Task))

    ''' <summary>
    ''' Отписывает обработчик от события потери связи с весами.
    ''' </summary>
    Sub UnsubscribeConnectionLost(handler As Func(Of Task))

    ''' <summary>
    ''' Подписывает обработчик на событие установления связи с весами.
    ''' </summary>
    Sub SubscribeConnectionEstablished(handler As Func(Of Task))

    ''' <summary>
    ''' Отписывает обработчик от события установления связи с весами.
    ''' </summary>
    Sub UnsubscribeConnectionEstablished(handler As Func(Of Task))

    ''' <summary>
    ''' Подписывает обработчик на событие аппаратной ошибки весов.
    ''' </summary>
    Sub SubscribeScaleAlarm(handler As Func(Of Task))

    ''' <summary>
    ''' Отписывает обработчик от события аппаратной ошибки весов.
    ''' </summary>
    Sub UnsubscribeScaleAlarm(handler As Func(Of Task))
End Interface