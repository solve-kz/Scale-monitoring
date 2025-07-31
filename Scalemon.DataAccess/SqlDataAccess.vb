
Imports System
Imports System.Data
Imports Microsoft.Data.SqlClient
Imports System.Threading.Tasks

''' <summary>
''' Реализация IDataAccess для MS SQL Express с буферизацией.
''' </summary>
Public Class SqlDataAccess
    Implements IDataAccess

    Private _connection As SqlConnection
    Private ReadOnly _buffer As New List(Of Tuple(Of Decimal, DateTime))()

    ''' <summary>
    ''' Строка подключения к базе.
    ''' </summary>
    Public Property ConnectionString As String Implements IDataAccess.ConnectionString


    ''' <summary>
    ''' Имя таблицы, в которую пишем.
    ''' </summary>
    Public Property TableName As String Implements IDataAccess.TableName

    ''' <summary>
    ''' Конструктор: передаём строку подключения и имя таблицы.
    ''' </summary>
    Public Sub New(connectionString As String, tableName As String)
        Me.ConnectionString = connectionString
        Me.TableName = tableName
    End Sub

    ''' <summary>
    ''' Открывает соединение к БД один раз.
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
    ''' Добавляет запись в буфер (не сразу в БД).
    ''' </summary>
    Public Async Function SaveWeighingAsync(weight As Decimal, timestamp As DateTime) As Task _
        Implements IDataAccess.SaveWeighingAsync

        SyncLock _buffer
            _buffer.Add(Tuple.Create(weight, timestamp))
        End SyncLock

        ' Асинхронная заглушка — чтобы метод возвращал Task
        Await Task.CompletedTask
    End Function

    ''' <summary>
    ''' Синхронно не вызывайте — используйте FlushAsync!
    ''' Берёт все накопленные записи и вставляет их в таблицу.
    ''' </summary>
    Public Async Function FlushAsync() As Task Implements IDataAccess.FlushAsync
        Dim itemsToInsert As List(Of Tuple(Of Decimal, DateTime))

        ' Копируем буфер и очищаем его под замком
        SyncLock _buffer
            If _buffer.Count = 0 Then
                Return
            End If
            itemsToInsert = New List(Of Tuple(Of Decimal, DateTime))(_buffer)
            _buffer.Clear()
        End SyncLock

        ' Вставляем все записи в рамках одной транзакции
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
    ''' Удаляет последнюю (по максимальному Timestamp) запись из таблицы.
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
    ''' Закрывает соединение и освобождает ресурсы.
    ''' </summary>
    Public Sub Dispose() Implements IDisposable.Dispose
        ' Перед закрытием можно ещё раз сбросить буфер:
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



