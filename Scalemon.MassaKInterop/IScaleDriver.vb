''' <summary>
''' Определяет низкоуровневый контракт для драйвера, напрямую взаимодействующего с COM-портом весов "Масса-К".
''' </summary>
Public Interface IScaleDriver
    ''' <summary>
    ''' Открывает соединение с COM-портом.
    ''' </summary>
    Sub OpenConnection()

    ''' <summary>
    ''' Закрывает соединение с COM-портом.
    ''' </summary>
    Sub CloseConnection()

    ''' <summary>
    ''' Отправляет команду сброса веса на ноль.
    ''' </summary>
    Sub SetToZero()

    ''' <summary>
    ''' Отправляет команду запроса текущего веса.
    ''' </summary>
    Sub ReadWeight()

    ''' <summary>
    ''' Имя COM-порта для подключения (например, "COM1").
    ''' </summary>
    Property PortConnection As String

    ''' <summary>
    ''' Возвращает последнее считанное значение веса.
    ''' </summary>
    ReadOnly Property Weight As Decimal

    ''' <summary>
    ''' Возвращает флаг, указывающий, является ли вес стабильным.
    ''' </summary>
    ReadOnly Property Stable As Boolean

    ''' <summary>
    ''' Возвращает числовой код последнего ответа от весов.
    ''' </summary>
    ReadOnly Property LastResponseNum As Long

    ''' <summary>
    ''' Возвращает текстовое описание последнего ответа от весов.
    ''' </summary>
    ReadOnly Property LastResponseText As String

    ''' <summary>
    ''' Возвращает флаг, указывающий, установлено ли соединение с портом.
    ''' </summary>
    ReadOnly Property isConnected As Boolean

    ''' <summary>
    ''' Возвращает флаг, указывающий на наличие аппаратной ошибки весов.
    ''' </summary>
    ReadOnly Property isScaleAlarm As Boolean
End Interface