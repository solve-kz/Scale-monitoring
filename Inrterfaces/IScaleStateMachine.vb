''' <summary>
''' Определяет контракт для конечного автомата (FSM), управляющего логикой взвешивания.
''' Предоставляет методы-обработчики для всех внешних событий системы.
''' </summary>
Public Interface IScaleStateMachine
    ''' <summary>
    ''' Обрабатывает событие установления связи с весами.
    ''' </summary>
    Function OnScaleConnectedAsync() As Task

    ''' <summary>
    ''' Обрабатывает событие потери связи с весами.
    ''' </summary>
    Function OnScaleDisconnectedAsync() As Task

    ''' <summary>
    ''' Обрабатывает событие, когда вес на платформе становится нестабильным.
    ''' </summary>
    Function OnScaleUnstableAsync() As Task

    ''' <summary>
    ''' Обрабатывает событие аппаратной ошибки весов.
    ''' </summary>
    Function OnScaleAlarmAsync() As Task

    ''' <summary>
    ''' Обрабатывает получение нового стабильного значения веса от процессора.
    ''' </summary>
    ''' <param name="raw">Необработанное значение веса.</param>
    Function OnWeightReceivedAsync(ByVal raw As Decimal) As Task

    ''' <summary>
    ''' Обрабатывает событие сбоя при записи в базу данных.
    ''' </summary>
    ''' <param name="ex">Исключение, вызвавшее сбой.</param>
    Function OnDatabaseFailedAsync(ex As Exception) As Task

    ''' <summary>
    ''' Обрабатывает событие восстановления связи с базой данных.
    ''' </summary>
    Function OnDatabaseRestoredAsync() As Task

    ''' <summary>
    ''' Обрабатывает событие нажатия кнопки на пульте Arduino.
    ''' </summary>
    Function OnButtonPressedAsync() As Task
End Interface
