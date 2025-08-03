

''' <summary>
''' Интерфейс для драйвера весов.
''' </summary>
Public Interface IScaleProcessor
    Sub Start()
    Sub [Stop]()
    Function ResetToZeroAsync() As Task
    Sub SubscribeWeightReceived(handler As Func(Of Decimal, Task))
    Sub UnsubscribeWeightReceived(handler As Func(Of Decimal, Task))
    Sub SubscribeUnstable(handler As Func(Of Task))
    Sub UnsubscribeUnstable(handler As Func(Of Task))
    Sub SubscribeConnectionLost(handler As Func(Of Task))
    Sub UnsubscribeConnectionLost(handler As Func(Of Task))
    Sub SubscribeConnectionEstablished(handler As Func(Of Task))
    Sub UnsubscribeConnectionEstablished(handler As Func(Of Task))
    Sub SubscribeScaleAlarm(handler As Func(Of Task))
    Sub UnsubscribeScaleAlarm(handler As Func(Of Task))


End Interface




