Imports Scalemon.Common




Public Interface ISignalBus
        ' Настройки подключения
        Property PortName As String
        Property BaudRate As Integer
        Property ReconnectIntervalMs As Integer

        ' События
        Event ConnectionEstablished()
        Event ConnectionLost()
        Event ButtonPressed()

        ' Метод отправки команды
        Sub Send(command As ArduinoSignalCode)

        ' Управление подключением
        Sub Start()
        Sub [Stop]()
    End Interface

