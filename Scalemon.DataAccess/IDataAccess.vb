' Объявляем типы делегатов для наших событий
Public Delegate Sub DatabaseFailedEventHandler(ex As Exception)
Public Delegate Sub DatabaseRestoredEventHandler()
Public Interface IDataAccess
    Inherits IDisposable

    ''' <summary>
    ''' Сохраняет значение веса и отметку времени в базу.
    ''' </summary>
    Function SaveWeighingAsync(weight As Decimal) As Task

    ''' <summary>
    ''' Удаляет последнюю запись (при необходимости отката).
    ''' </summary>
    Function DeleteLastWeighingAsync() As Task

    ' Теперь объявляем события, используя наши новые типы делегатов
    Event DatabaseFailed As DatabaseFailedEventHandler
    Event DatabaseRestored As DatabaseRestoredEventHandler

End Interface


