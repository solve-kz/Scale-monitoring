Imports System
Imports System.Collections.Concurrent
Imports System.Data
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.SqlClient
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Scalemon.Common

Public Class SqlDataAccess
    Implements Scalemon.Common.IDataAccess, IDisposable

    Private ReadOnly _logger As ILogger(Of SqlDataAccess)
    Private ReadOnly _retryQueue As New ConcurrentQueue(Of Decimal)
    Private ReadOnly _lifetime As IHostApplicationLifetime
    Private ReadOnly _connString As String
    Private ReadOnly _tableName As String
    Private ReadOnly _maxQueueSize As Integer
    Private _isDbDown As Boolean = False ' Флаг, указывающий на состояние БД
    Private _retryCount As Decimal
    Private _alarmSize As Integer
    Private ReadOnly _syncLock As New Object()

    Public Event DatabaseFailed As DatabaseFailedEventHandler Implements IDataAccess.DatabaseFailed
    Public Event DatabaseRestored As DatabaseRestoredEventHandler Implements IDataAccess.DatabaseRestored

    Public Sub New(logger As ILogger(Of SqlDataAccess),
                  connString As String,
                  tableName As String,
                  maxQueueSize As Integer,
                  alarmsize As Integer,
                  lifetime As IHostApplicationLifetime)

        _logger = logger
        _connString = connString
        _tableName = tableName
        _lifetime = lifetime ' <-- Сохраняем зависимость
        _maxQueueSize = maxQueueSize
        _alarmSize = alarmsize
        StartRetryLoop(_lifetime.ApplicationStopping) ' <-- Передаем токен отмены
    End Sub

    Private Sub StartRetryLoop(cancellationToken As CancellationToken)
        Task.Run(Async Function()
                     Try
                         ' Цикл работает, пока не поступит запрос на остановку
                         While Not cancellationToken.IsCancellationRequested
                             ' Ждем 30 секунд, но прерываем ожидание, если пришел сигнал остановки
                             Await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)
                             Dim localList As New List(Of Decimal)

                             ' убираем из очереди всё, что накопилось
                             Dim w As Decimal
                             While _retryQueue.TryDequeue(w)
                                 localList.Add(w)
                             End While

                             ' ѕытаемс¤ снова
                             For Each weight In localList
                                 Try
                                     Await WriteToDatabaseAsync(weight)
                                     ' ЕСЛИ МЫ ЗДЕСЬ, ЗНАЧИТ ЗАПИСЬ ПРОШЛА УСПЕШНО
                                     ' ПРОВЕРЯЕМ, БЫЛИ ЛИ МЫ В СОСТОЯНИИ СБОЯ
                                     Dim wasDown As Boolean = False
                                     SyncLock _syncLock
                                         If _isDbDown Then
                                             _isDbDown = False
                                             wasDown = True
                                         End If
                                     End SyncLock

                                     If wasDown Then
                                         RaiseEvent DatabaseRestored()
                                     End If
                                 Catch
                                     ' если не получилось, возвращаем в очередь
                                     _retryQueue.Enqueue(weight)
                                 End Try
                             Next

                         End While
                     Catch ex As TaskCanceledException
                         ' нормально завершаемся
                     Finally
                         ' Финальная попытка сохранить оставшиеся данные
                         Dim finalItems As New List(Of Decimal)()
                         Dim w As Decimal
                         While _retryQueue.TryDequeue(w)
                             finalItems.Add(w)
                         End While

                         For Each weight In finalItems
                             Try
                                 Using conn As New SqlConnection(_connString)
                                     conn.Open()
                                     Dim sql = $"INSERT INTO {_tableName} (Weight, RecordedAt) VALUES (@weight, GETDATE());"
                                     Using cmd As New SqlCommand(sql, conn)
                                         cmd.Parameters.Add("@weight", SqlDbType.Decimal).Value = weight
                                         cmd.ExecuteNonQuery()
                                     End Using
                                 End Using
                             Catch
                                 _logger.LogError("Lost data on shutdown: {weight}", weight)
                             End Try
                         Next
                     End Try
                 End Function)
    End Sub

    Public Async Function SaveWeighingAsync(weight As Decimal) _
    As Task Implements IDataAccess.SaveWeighingAsync
        Try
            ' попытка записать сразу
            Await WriteToDatabaseAsync(weight)
            SyncLock _syncLock
                _retryCount = 0
                If _isDbDown Then
                    _isDbDown = False
                    RaiseEvent DatabaseRestored()
                End If
            End SyncLock
        Catch ex As Exception
            ' ПРОВЕРКА ПЕРЕД ДОБАВЛЕНИЕМ В ОЧЕРЕДЬ
            If _retryQueue.Count < _maxQueueSize Then
                _retryQueue.Enqueue(weight)
            Else
                ' ОЧЕРЕДЬ ПЕРЕПОЛНЕНА! Это критическая ситуация.
                ' Здесь мы вынуждены отбросить взвешивание, но должны
                ' обязательно залогировать это как КРИТИЧЕСКУЮ ОШИБКУ.
                _logger.LogError("Retry queue is full. Losing data: {weight}", weight)
                ' Фиксируем состояние недоступности БД и уведомляем FSM
                SyncLock _syncLock
                    If Not _isDbDown Then
                        _isDbDown = True
                        RaiseEvent DatabaseFailed(ex)
                    End If
                End SyncLock
                Throw New InvalidOperationException("Очередь повторной записи переполнена. Данные теряются.")
            End If
            ' Отмечаем первую недоступность БД
            Dim notifyFailed As Boolean = False
            SyncLock _syncLock
                If Not _isDbDown Then
                    _isDbDown = True
                    notifyFailed = True
                End If
                _retryCount += 1
            End SyncLock

            If notifyFailed OrElse _retryCount > _alarmSize Then
                RaiseEvent DatabaseFailed(ex)
            End If
        End Try
    End Function

    Private Async Function WriteToDatabaseAsync(weight As Decimal) As Task
        Using conn As New SqlConnection(_connString)
            Await conn.OpenAsync()
            Dim sql = $"INSERT INTO {_tableName} (Weight, RecordedAt) VALUES (@weight, GETDATE());"
            Using cmd As New SqlCommand(sql, conn)
                cmd.Parameters.Add("@weight", SqlDbType.Decimal).Value = weight
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Public Async Function DeleteLastWeighingAsync() As Task _
        Implements IDataAccess.DeleteLastWeighingAsync
        Using conn As New SqlConnection(_connString)
            Await conn.OpenAsync()
            Using cmd As New SqlCommand(
                $"DELETE TOP (1) FROM {_tableName} ORDER BY RecordedAt DESC", conn)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        ' «десь ничего не хранитс¤ между вызовами, так что можно оставить пустым.
    End Sub
End Class



