
Public Interface IDataAccess
    Inherits IDisposable

    ''' <summary>
    ''' Сохраняет значение веса и отметку времени в базу.
    ''' </summary>
    Function SaveWeighingAsync(weight As Decimal, timestamp As DateTime) As Task

    ''' <summary>
    ''' Удаляет последнюю запись (при необходимости отката).
    ''' </summary>
    Function DeleteLastWeighingAsync() As Task

End Interface


