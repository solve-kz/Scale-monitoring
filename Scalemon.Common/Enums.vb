


Public Enum ArduinoSignalCode As Byte
    LinkOn = &H10 ' Включается индикатор связи с весами
    LinkOff = &H11 ' Выключаются все четыре индикатора
    Idle = &H12 ' Включается зеленая лампа
    Unstuble = &H13 ' Выключаются зеленая и желтая лампы
    Complited = &H14 ' Включается желтая лампа
    YellowRedOn = &H15 ' Включаются желтая и красная лампы
    RedOn = &H16 ' Включается красная лампа
    AlarmOff = &H17 ' Выключается красная лампа
End Enum

Public Enum ScalesState
    Disconnected       ' весы не подключены
    Connected          ' общий суперстатус «подключены»
    Unstable            ' вес нестабилизирован
    Stabilized          ' вес стабилизирован — дальше подкатегории
    NegativeWeight          ' raw < 0
    ZeroWeight              ' raw == 0
    LightWeight             ' 0 < raw <= h
    InvalidWeight           ' h < raw <= minWeight
    ReadyToRecord           ' raw > minWeight — готовность к записи
    ErrorAfterWeighing          ' > minWeight, но без предварительного нуля
    Recorded                    ' вес записан в БД
    ScaleError          ' аппаратная ошибка весов
End Enum


Public Enum Trigger

    ScaleConnected       ' Scale.ConnectionEstablished
    ScaleDisconnected     ' Scale.ConnectionLost
    ScaleStabilized       ' при срабатывании порога стабильности
    ReadinessToScale        ' при готовности к взвешиванию
    ResetToZero          'необходимость сброса веса к нулю
    ScaleUnstable         ' Scale.Unstable
    ScaleAlarm            ' Scale.ScaleAlarm
    WeightReceived        ' общий триггер: приходит необработанный raw
    ArduinoButtonPressed  ' Arduino.ButtonPressed — сброс «нулевого» флага
    ' Внутренние «результаты» для ReadyToRecord
    RecordSuccess
    RecordFailure
End Enum

