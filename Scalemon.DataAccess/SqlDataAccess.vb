Imports System
Imports System.Data
Imports Microsoft.Data.SqlClient
Imports System.Threading.Tasks


Public Class SqlDataAccess
    Implements IDataAccess
    Private ReadOnly _connString As String
    Private ReadOnly _tableName As String
    Public Sub New(connString As String, tableName As String)
        _connString = connString
        _tableName = tableName
    End Sub

    Public Async Function SaveWeighingAsync(weight As Decimal, timestamp As DateTime) _
        As Task Implements IDataAccess.SaveWeighingAsync

        Using conn As New SqlConnection(_connString)
            Await conn.OpenAsync()
            Using cmd As New SqlCommand(
                $"INSERT INTO {_tableName} (RawValue, Timestamp) VALUES (@raw, @ts)", conn)

                cmd.Parameters.AddWithValue("@raw", weight)
                cmd.Parameters.AddWithValue("@ts", timestamp)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function


    Public Async Function DeleteLastWeighingAsync() _
       As Task Implements IDataAccess.DeleteLastWeighingAsync

        Using conn As New SqlConnection(_connString)
            Await conn.OpenAsync()
            Using cmd As New SqlCommand(
                $"DELETE TOP (1) FROM {_tableName} ORDER BY Timestamp DESC", conn)

                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Здесь ничего не хранится между вызовами, так что можно оставить пустым.
    End Sub


End Class



