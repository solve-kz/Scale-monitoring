
Imports System
Imports System.Data
Imports Microsoft.Data.SqlClient
Imports System.Threading.Tasks

''' <summary>
''' ���������� IDataAccess ��� MS SQL Express � ������������.
''' </summary>
Public Class SqlDataAccess
    Implements IDataAccess

    Private _connection As SqlConnection
    Private ReadOnly _buffer As New List(Of Tuple(Of Decimal, DateTime))()

    ''' <summary>
    ''' ������ ����������� � ����.
    ''' </summary>
    Public Property ConnectionString As String Implements IDataAccess.ConnectionString


    ''' <summary>
    ''' ��� �������, � ������� �����.
    ''' </summary>
    Public Property TableName As String Implements IDataAccess.TableName

    ''' <summary>
    ''' �����������: ������� ������ ����������� � ��� �������.
    ''' </summary>
    Public Sub New(connectionString As String, tableName As String)
        Me.ConnectionString = connectionString
        Me.TableName = tableName
    End Sub

    ''' <summary>
    ''' ��������� ���������� � �� ���� ���.
    ''' </summary>
    Public Async Function OpenAsync() As Task Implements IDataAccess.OpenAsync
        If _connection Is Nothing Then
            _connection = New SqlConnection(ConnectionString)
        End If

        If _connection.State <> ConnectionState.Open Then
            Await _connection.OpenAsync()
        End If
    End Function

    ''' <summary>
    ''' ��������� ������ � ����� (�� ����� � ��).
    ''' </summary>
    Public Async Function SaveWeighingAsync(weight As Decimal, timestamp As DateTime) As Task _
        Implements IDataAccess.SaveWeighingAsync

        SyncLock _buffer
            _buffer.Add(Tuple.Create(weight, timestamp))
        End SyncLock

        ' ����������� �������� � ����� ����� ��������� Task
        Await Task.CompletedTask
    End Function

    ''' <summary>
    ''' ��������� �� ��������� � ����������� FlushAsync!
    ''' ���� ��� ����������� ������ � ��������� �� � �������.
    ''' </summary>
    Public Async Function FlushAsync() As Task Implements IDataAccess.FlushAsync
        Dim itemsToInsert As List(Of Tuple(Of Decimal, DateTime))

        ' �������� ����� � ������� ��� ��� ������
        SyncLock _buffer
            If _buffer.Count = 0 Then
                Return
            End If
            itemsToInsert = New List(Of Tuple(Of Decimal, DateTime))(_buffer)
            _buffer.Clear()
        End SyncLock

        ' ��������� ��� ������ � ������ ����� ����������
        Using transaction = _connection.BeginTransaction()
            Using cmd As New SqlCommand() With {
                .Connection = _connection,
                .transaction = transaction,
                .CommandText = $"INSERT INTO [{TableName}] ([Weight], [Timestamp]) VALUES (@w, @t)"
            }
                cmd.Parameters.Add("@w", SqlDbType.Decimal)
                cmd.Parameters.Add("@t", SqlDbType.DateTime)

                For Each item In itemsToInsert
                    cmd.Parameters("@w").Value = item.Item1
                    cmd.Parameters("@t").Value = item.Item2
                    Await cmd.ExecuteNonQueryAsync()
                Next
            End Using
            transaction.Commit()
        End Using
    End Function

    ''' <summary>
    ''' ������� ��������� (�� ������������� Timestamp) ������ �� �������.
    ''' </summary>
    Public Async Function DeleteLastWeighingAsync() As Task _
        Implements IDataAccess.DeleteLastWeighingAsync

        Dim sql = $"
            DELETE TOP (1)
            FROM [{TableName}]
            WHERE [Timestamp] = (
                SELECT MAX([Timestamp])
                FROM [{TableName}]
            )"

        Using cmd As New SqlCommand(sql, _connection)
            Await cmd.ExecuteNonQueryAsync()
        End Using
    End Function

    ''' <summary>
    ''' ��������� ���������� � ����������� �������.
    ''' </summary>
    Public Sub Dispose() Implements IDisposable.Dispose
        ' ����� ��������� ����� ��� ��� �������� �����:
        FlushAsync().GetAwaiter().GetResult()

        If _connection IsNot Nothing Then
            If _connection.State <> ConnectionState.Closed Then
                _connection.Close()
            End If
            _connection.Dispose()
            _connection = Nothing
        End If
    End Sub

End Class



