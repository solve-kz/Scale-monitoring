

''' <summary>
''' Интерфейс для драйвера весов.
''' </summary>
Public Interface IScaleProcessor
        Event WeightReceived(raw As Decimal)
        Event Unstable()
        Event ConnectionLost()
        Event ConnectionEstablished()
        Event ScaleAlarm()
        Sub Start()
        Sub [Stop]()
        Sub ResetToZero()



    End Interface




