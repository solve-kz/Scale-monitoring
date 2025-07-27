

''' <summary>
''' Интерфейс для конечного автомата.
''' </summary>

Public Interface IStateMachine
    Event StateChanged(newState As Common.ScaleState)
    ReadOnly Property LastWeight As Decimal
    Sub HandleWeight(raw As Decimal)
    Sub HandleArduinoConnectionLost()
    Sub HandleArduinoConnectionEstablished()
    Sub HandleButtonPressed()

End Interface


