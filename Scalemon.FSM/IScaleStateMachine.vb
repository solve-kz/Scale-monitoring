''' <summary>
''' Интерфейс для взаимодействия с конечным автоматом состояния весов.
''' Методы соответствуют внешним событиям от Scale и Arduino.
''' </summary>
Public Interface IScaleStateMachine

    ''' <summary>Вызывается при установлении связи с весами.</summary>
    Function OnScaleConnectedAsync() As Task

    ''' <summary>Вызывается при потере связи с весами.</summary>
    Function OnScaleDisconnectedAsync() As Task


    ''' <summary>Вызывается, когда весы стали нестабильными.</summary>
    Function OnScaleUnstableAsync() As Task

    ''' <summary>Вызывается при возникновении аппаратной ошибки весов.</summary>
    Function OnScaleAlarmAsync() As Task


    ''' <summary>
    ''' Вызывается при получении нового значения веса.
    ''' <paramref name="raw"/> — необработанное значение веса.
    ''' </summary>
    Function OnWeightReceivedAsync(ByVal raw As Decimal) As Task

    ' Новый метод, который принимает Exception
    Function OnDatabaseFailedAsync(ex As Exception) As Task

    ' Новый метод для восстановления
    Function OnDatabaseRestoredAsync() As Task

    ''' <summary>Вызывается при нажатии кнопки сброса на Arduino.</summary>
    Function OnButtonPressedAsync() As Task

End Interface
