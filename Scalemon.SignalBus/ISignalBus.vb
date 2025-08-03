Imports Scalemon.Common




Public Interface ISignalBus
        ' Настройки подключения
        Property PortName As String
        Property BaudRate As Integer
        Property ReconnectIntervalMs As Integer

        ' События
        Event ConnectionEstablished()
        Event ConnectionLost()
    Sub SubscribeButtonPressed(handler As Func(Of Task))
    Sub UnsubscribeButtonPressed(handler As Func(Of Task))

    ' Метод отправки команды
    Function SendAsync(cmd As ArduinoSignalCode) As Task

    ' Управление подключением
    Sub Start()
        Sub [Stop]()
    End Interface

