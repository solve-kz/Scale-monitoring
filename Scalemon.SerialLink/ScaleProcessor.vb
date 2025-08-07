Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports Scalemon.MassaKInterop

''' <summary>
''' Компонент, выполняющий периодический опрос весов через драйвер,
''' определяет, стабилизировался ли вес, и передаёт соответствующие события подписчикам.
''' </summary>
Public Class ScaleProcessor
    Implements Scalemon.Common.IScaleProcessor, IDisposable

    ' Списки обработчиков для событий
    Private ReadOnly _weightHandlers As New List(Of Func(Of Decimal, Task))()
    Private ReadOnly _unstableHandlers As New List(Of Func(Of Task))()
    Private ReadOnly _connectionLostHandlers As New List(Of Func(Of Task))()
    Private ReadOnly _connectionEstablishedHandlers As New List(Of Func(Of Task))()
    Private ReadOnly _scaleAlarmHandlers As New List(Of Func(Of Task))()

    ' Драйвер весов
    Private ReadOnly _driver As IScaleDriver

    ' Логгер для событий и ошибок
    Private ReadOnly _logger As ILogger(Of ScaleProcessor)

    ' Параметры, считываемые из конфигурации
    Private ReadOnly _stableThreshold As Integer
    Private ReadOnly _unstableThreshold As Integer
    Private ReadOnly _pollingInterval As Integer

    ' Для управления жизненным циклом фоновой задачи
    Private _cts As CancellationTokenSource
    Private _processingTask As Task
    Private disposedValue As Boolean

    ''' <summary>
    ''' Конструктор: инициализирует драйвер и параметры из конфигурации.
    ''' </summary>
    Public Sub New(logger As ILogger(Of ScaleProcessor), driver As Scalemon.Common.IScaleDriver, portName As String, stableThreshold As Integer, unstableThreshold As Integer, pollingIntervalMs As Integer)
        _logger = logger
        _driver = driver
        driver.PortConnection = portName
        _stableThreshold = stableThreshold
        _unstableThreshold = unstableThreshold
        _pollingInterval = pollingIntervalMs
        _logger.LogDebug("ScaleProcessor initialized: PollInterval={interval}ms, StableThreshold={stable}, UnstableThreshold={unstable}", _pollingInterval, _stableThreshold, _unstableThreshold)
    End Sub

    ''' <summary>
    ''' Запускает фоновую задачу опроса весов.
    ''' </summary>
    Public Sub Start() Implements Scalemon.Common.IScaleProcessor.Start
        _cts = New CancellationTokenSource()
        _processingTask = ProcessLoopAsync(_cts.Token)
        _logger.LogInformation("ScaleProcessor started")
    End Sub

    ''' <summary>
    ''' Главный цикл опроса весов, выполняется с интервалом _pollingInterval.
    ''' </summary>
    Private Async Function ProcessLoopAsync(token As CancellationToken) As Task
        Dim timer = New PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval))
        Dim stableCount As Integer = 0
        Dim unstableCount As Integer = 0
        Try
            While Await timer.WaitForNextTickAsync(token)
                Dim shouldNotifyLost As Boolean = False
                Try
                    ' Подключение к весам при необходимости
                    If Not _driver.isConnected Then
                        _driver.OpenConnection()
                        If _driver.isConnected Then
                            Await RaiseAllAsync(_connectionEstablishedHandlers)
                        End If
                    End If

                    If _driver.isConnected Then
                        _driver.ReadWeight()
                        Select Case _driver.LastResponseNum
                            Case 0
                                ' Ответ корректный: проверка на стабилизацию веса
                                If _driver.Stable Then
                                    stableCount += 1
                                    unstableCount = 0
                                    If stableCount >= _stableThreshold Then
                                        Await RaiseAllAsync(_weightHandlers, _driver.Weight)
                                        stableCount = 0
                                    End If
                                Else
                                    unstableCount += 1
                                    stableCount = 0
                                    If unstableCount >= _unstableThreshold Then
                                        Await RaiseAllAsync(_unstableHandlers)
                                        unstableCount = 0
                                    End If
                                End If
                            Case 1
                                ' Весы вернули ошибку — разрываем соединение
                                _driver.CloseConnection()
                                shouldNotifyLost = True
                            Case Else
                                ' Аппаратная ошибка (например, ALARM)
                                Await RaiseAllAsync(_scaleAlarmHandlers)
                        End Select
                    End If
                Catch ex As Exception
                    ' Любая ошибка — считаем потерей связи
                    _logger.LogError(ex, "Error during weight poll cycle")
                    _driver.CloseConnection()
                    shouldNotifyLost = True
                End Try

                ' Отдельно уведомляем о потере связи (вне Catch)
                If shouldNotifyLost Then
                    Await RaiseAllAsync(_connectionLostHandlers)
                End If
            End While
        Catch ocex As OperationCanceledException
            ' Ожидаемая отмена при остановке
        Finally
            timer.Dispose()
        End Try
    End Function

    ''' <summary>
    ''' Останавливает опрос весов и закрывает соединение.
    ''' </summary>
    Public Sub [Stop]() Implements Scalemon.Common.IScaleProcessor.Stop
        If _cts IsNot Nothing Then
            _cts.Cancel()
            Try
                _processingTask?.Wait()
            Catch ex As AggregateException
                ' Игнорируем отмену
            End Try
        End If
        _driver.CloseConnection()
        _logger.LogInformation("ScaleProcessor stopped")
    End Sub

    ''' <summary>
    ''' Отправляет команду сброса веса в 0.
    ''' </summary>
    Public Async Function ResetToZeroAsync() As Task Implements Scalemon.Common.IScaleProcessor.ResetToZeroAsync
        _driver.SetToZero()
        If _driver.LastResponseNum > 0 Then
            _logger.LogError("Error resetting to zero: {text}", _driver.LastResponseText)
            Throw New InvalidOperationException($"Error resetting to zero: {_driver.LastResponseText}")
        End If
        Await Task.CompletedTask
    End Function

    ''' <summary>
    ''' Корректное освобождение ресурсов и остановка фоновой задачи.
    ''' </summary>
    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                If _cts IsNot Nothing Then
                    _cts.Cancel()
                    _cts.Dispose()
                End If
                _driver.CloseConnection()
            End If
            disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub

    ''' <summary>
    ''' Безопасный вызов всех обработчиков события с параметром.
    ''' </summary>
    Private Async Function RaiseAllAsync(Of T)(handlers As IEnumerable(Of Func(Of T, Task)), arg As T) As Task
        For Each h In handlers.ToArray()
            Try
                Await h(arg)
            Catch ex As Exception
                _logger.LogError(ex, "Handler error")
            End Try
        Next
    End Function

    ''' <summary>
    ''' Безопасный вызов всех обработчиков события без параметров.
    ''' </summary>
    Private Async Function RaiseAllAsync(handlers As IEnumerable(Of Func(Of Task))) As Task
        For Each h In handlers.ToArray()
            Try
                Await h()
            Catch ex As Exception
                _logger.LogError(ex, "Handler error")
            End Try
        Next
    End Function

    ' Методы подписки/отписки на события:
    Public Sub SubscribeWeightReceived(handler As Func(Of Decimal, Task)) Implements Scalemon.Common.IScaleProcessor.SubscribeWeightReceived
        _weightHandlers.Add(handler)
    End Sub
    Public Sub UnsubscribeWeightReceived(handler As Func(Of Decimal, Task)) Implements Scalemon.Common.IScaleProcessor.UnsubscribeWeightReceived
        _weightHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeUnstable(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.SubscribeUnstable
        _unstableHandlers.Add(handler)
    End Sub
    Public Sub UnsubscribeUnstable(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.UnsubscribeUnstable
        _unstableHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeConnectionLost(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.SubscribeConnectionLost
        _connectionLostHandlers.Add(handler)
    End Sub
    Public Sub UnsubscribeConnectionLost(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.UnsubscribeConnectionLost
        _connectionLostHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeConnectionEstablished(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.SubscribeConnectionEstablished
        _connectionEstablishedHandlers.Add(handler)
    End Sub
    Public Sub UnsubscribeConnectionEstablished(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.UnsubscribeConnectionEstablished
        _connectionEstablishedHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeScaleAlarm(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.SubscribeScaleAlarm
        _scaleAlarmHandlers.Add(handler)
    End Sub
    Public Sub UnsubscribeScaleAlarm(handler As Func(Of Task)) Implements Scalemon.Common.IScaleProcessor.UnsubscribeScaleAlarm
        _scaleAlarmHandlers.Remove(handler)
    End Sub
End Class
