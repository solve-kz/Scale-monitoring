''' <summary>
''' Делегат события ошибки базы данных.
''' </summary>
Public Delegate Sub DatabaseFailedEventHandler(ex As Exception)

''' <summary>
''' Делегат события восстановления базы данных.
''' </summary>
Public Delegate Sub DatabaseRestoredEventHandler()

''' <summary>
''' Интерфейс доступа к данным для записи взвешиваний.
''' Поддерживает асинхронную запись, удаление и уведомление о сбоях БД.
''' </summary>
Public Interface IDataAccess
    Inherits IDisposable

    ''' <summary>
    ''' Асинхронно сохраняет значение веса и текущую отметку времени в базу данных.
    ''' </summary>
    ''' <param name="weight">Значение веса для сохранения.</param>
    Function SaveWeighingAsync(weight As Decimal) As Task

    ''' <summary>
    ''' Асинхронно удаляет последнюю сделанную запись (используется для отката ошибочных действий).
    ''' </summary>
    Function DeleteLastWeighingAsync() As Task

    ''' <summary>
    ''' Событие, возникающее при сбое подключения или выполнения запроса к базе данных.
    ''' </summary>
    Event DatabaseFailed As DatabaseFailedEventHandler

    ''' <summary>
    ''' Событие, возникающее при успешном восстановлении связи с базой данных после сбоя.
    ''' </summary>
    Event DatabaseRestored As DatabaseRestoredEventHandler
End Interface