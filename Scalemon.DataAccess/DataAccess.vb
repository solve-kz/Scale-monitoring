
Public Class DataAccess
        Implements IDataAccess

        Public Sub Flush() Implements IDataAccess.Flush
            Throw New NotImplementedException()
        End Sub

    Public Function SaveWeighing(weight As Decimal, timestamp As Date) As Task Implements IDataAccess.SaveWeighing
        Throw New NotImplementedException()
    End Function
End Class


