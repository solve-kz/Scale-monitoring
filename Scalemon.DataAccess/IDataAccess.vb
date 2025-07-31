
Public Interface IDataAccess
    Inherits IDisposable

    Property ConnectionString As String
    Property TableName As String

    Function OpenAsync() As Task
    Function SaveWeighingAsync(weight As Decimal, timestamp As DateTime) As Task
    Function DeleteLastWeighingAsync() As Task
    Function FlushAsync() As Task
End Interface


