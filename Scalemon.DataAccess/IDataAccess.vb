
Public Interface IDataAccess
    Function SaveWeighing(weight As Decimal, timestamp As DateTime) As Task
    Sub Flush()
    End Interface

