Imports Scalemon.Common

''' <summary>
''' Интерфейс для управления сигнализацией через Arduino.
''' Отправка команд и обработка событий подключения.
''' </summary>
Public Interface ISignalBus

    ''' <summary>Событие установления подключения к Arduino.</summary>
    Event ConnectionEstablished()

    ''' <summary>Событие потери подключения к Arduino.</summary>
    Event ConnectionLost()

    ''' <summary>Подписаться на нажатие кнопки.</summary>
    Sub SubscribeButtonPressed(handler As Func(Of Task))

    ''' <summary>Отписаться от нажатия кнопки.</summary>
    Sub UnsubscribeButtonPressed(handler As Func(Of Task))

    ''' <summary>Отправляет команду на Arduino.</summary>
    Function SendAsync(cmd As ArduinoSignalCode) As Task

    ''' <summary>Запускает обработку событий и подключение.</summary>
    Sub Start()

    ''' <summary>Останавливает обработку и отключает порт.</summary>
    Sub [Stop]()

End Interface