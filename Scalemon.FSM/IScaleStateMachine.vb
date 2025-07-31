''' <summary>
''' Интерфейс для взаимодействия с конечным автоматом состояния весов.
''' Методы соответствуют внешним событиям от Scale и Arduino.
''' </summary>
Public Interface IScaleStateMachine

    ''' <summary>Вызывается при установлении связи с весами.</summary>
    Sub OnScaleConnected()

    ''' <summary>Вызывается при потере связи с весами.</summary>
    Sub OnScaleDisconnected()

    ''' <summary>Вызывается, когда весы перешли в стабилизированное состояние.</summary>
    Sub OnScaleStabilized()

    ''' <summary>Вызывается, когда весы стали нестабильными.</summary>
    Sub OnScaleUnstable()

    ''' <summary>Вызывается при возникновении аппаратной ошибки весов.</summary>
    Sub OnScaleAlarm()

    ''' <summary>Вызывается при необходимости сброса в 0.</summary>
    Sub OnResetToZero()


    ''' <summary>Вызывается при стабилизации весов на 0).</summary>
    Sub OnZeroState()


    ''' <summary>
    ''' Вызывается при получении нового значения веса.
    ''' <paramref name="raw"/> — необработанное значение веса.
    ''' </summary>
    Sub OnWeightReceived(ByVal raw As Decimal)

    ''' <summary>Вызывается при нажатии кнопки сброса на Arduino.</summary>
    Sub OnButtonPressed()

End Interface
