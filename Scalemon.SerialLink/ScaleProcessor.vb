Imports System.Threading
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports Scalemon.MassaKInterop

Public Class ScaleProcessor
    Implements IScaleProcessor, IDisposable

    Private ReadOnly _weightHandlers As New List(Of Func(Of Decimal, Task))()
    Private ReadOnly _unstableHandlers As New List(Of Func(Of Task))()
    Private ReadOnly _connectionlostHandlers As New List(Of Func(Of Task))()
    Private ReadOnly _connectionestablishedHandlers As New List(Of Func(Of Task))()
    Private ReadOnly _scalealarmHandlers As New List(Of Func(Of Task))()

    Private _isProcessing As Boolean = False

    Private ReadOnly _driver As IScaleDriver
    Private ReadOnly _logger As ILogger(Of ScaleProcessor)
    Private ReadOnly _config As IConfiguration
    Private WithEvents Timer As New Timers.Timer
    Private _stableCount As Integer = 0
    Private _unstableCount As Integer = 0
    Private disposedValue As Boolean = False
    Private shouldRaiseConnectionLost As Boolean = False

    Public Sub New(config As IConfiguration, logger As ILogger(Of ScaleProcessor))
        _config = config
        _logger = logger
        _driver = New ScaleDriver100()
        _driver.PortConnection = _config("ScaleSettings:PortName")
        _logger.LogDebug("Создан объект процессора весов")
    End Sub


    Public Async Sub Start() Implements IScaleProcessor.Start
        ' Реализация запуска драйвера весов
        Try
            _driver.OpenConnection()
        Catch ex As Exception
            _logger.LogError(ex, "Ошибка при открытии соединения с весами")
        End Try
        If _driver.isConnected Then
            Await RaiseAllAsync(_connectionestablishedHandlers)
        Else
            Await RaiseAllAsync(_connectionlostHandlers)
        End If
        Timer.Interval = Integer.Parse(_config("ScaleSettings:PollingIntervalMs"))
        Timer.AutoReset = False
        Timer.Start()

    End Sub

    Private Async Sub OnTimerElapsed(sender As Object, e As Timers.ElapsedEventArgs) Handles Timer.Elapsed
        ' 1. ПРОВЕРКА ФЛАГА: Если предыдущий тик еще работает - выходим
        If _isProcessing Then
            _logger.LogWarning("Пропущен тик таймера, так как предыдущая операция еще не завершена.")
            Return
        End If
        Try
            _isProcessing = True
            If Not _driver.isConnected Then
                _driver.OpenConnection()
                If _driver.isConnected Then Await RaiseAllAsync(_connectionestablishedHandlers)
            End If
            If _driver.isConnected Then
                _driver.ReadWeight()
                Select Case _driver.LastResponseNum
                    Case 0
                        If _driver.Stable Then
                            _stableCount += 1
                            _unstableCount = 0
                            If _stableCount = _config("ScaleSettings:StableThreshold") Then
                                Await RaiseAllAsync(_weightHandlers, _driver.Weight)
                                _stableCount = 0
                            End If
                        Else
                            _unstableCount += 1
                            _stableCount = 0
                            If _unstableCount = _config("ScaleSettings:UnstableThreshold") Then
                                Await RaiseAllAsync(_unstableHandlers)
                                _unstableCount = 0
                            End If
                        End If
                    Case 1
                        ' Обработка ошибки 
                        _driver.CloseConnection()
                        Await RaiseAllAsync(_connectionlostHandlers)
                    Case Else
                        Await RaiseAllAsync(_scalealarmHandlers)
                End Select
            End If
        Catch ex As Exception
            _logger.LogError(ex, "Ошибка при опросе весов")
            _driver.CloseConnection()
            shouldRaiseConnectionLost = True
        Finally
            _isProcessing = False
            Timer.Start()
        End Try
        If shouldRaiseConnectionLost Then
            Await RaiseAllAsync(_connectionlostHandlers)
        End If
    End Sub

    Public Sub [Stop]() Implements IScaleProcessor.Stop
        ' Реализация остановки драйвера весов
        Timer.Stop()
        _driver.CloseConnection()
    End Sub
    Public Async Function ResetToZeroAsync() As Task Implements IScaleProcessor.ResetToZeroAsync
        _driver.SetToZero()
        If _driver.LastResponseNum > 0 Then
            _logger.LogError("Ошибка сброса на ноль")
            Throw New InvalidOperationException($"Ошибка сброса на ноль: {_driver.LastResponseText}")
        End If
        Await Task.CompletedTask
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' === Здесь освобождаем все управляемые ресурсы ===
                ' Останавливаем и уничтожаем таймер
                If Timer IsNot Nothing Then
                    Timer.Stop()
                    Timer.Dispose()
                End If

                ' Закрываем COM-порт драйвера
                Try
                    _driver.CloseConnection()
                Catch
                    ' игнорируем ошибки при закрытии
                End Try
            End If

            ' === Здесь можно освободить неуправляемые ресурсы, если бы они были ===

            disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Не изменяйте этот код. Разместите код очистки в методе "Dispose(disposing As Boolean)".
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
    Protected Overrides Sub Finalize()
        ' в финализаторе освобождаем только неуправляемые
        Dispose(disposing:=False)
        MyBase.Finalize()
    End Sub

    Private Async Function RaiseAllAsync(Of T)(handlers As IEnumerable(Of Func(Of T, Task)), arg As T) As Task
        For Each h In handlers.ToArray()
            Await h(arg)
        Next
    End Function

    Private Async Function RaiseAllAsync(handlers As IEnumerable(Of Func(Of Task))) As Task
        For Each h In handlers.ToArray()
            Await h()
        Next
    End Function

    Public Sub SubscribeWeightReceived(handler As Func(Of Decimal, Task)) Implements IScaleProcessor.SubscribeWeightReceived
        _weightHandlers.Add(handler)
    End Sub

    Public Sub UnsubscribeWeightReceived(handler As Func(Of Decimal, Task)) Implements IScaleProcessor.UnsubscribeWeightReceived
        _weightHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeUnstable(handler As Func(Of Task)) Implements IScaleProcessor.SubscribeUnstable
        _unstableHandlers.Add(handler)
    End Sub

    Public Sub UnsubscribeUnstable(handler As Func(Of Task)) Implements IScaleProcessor.UnsubscribeUnstable
        _unstableHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeConnectionLost(handler As Func(Of Task)) Implements IScaleProcessor.SubscribeConnectionLost
        _connectionlostHandlers.Add(handler)
    End Sub

    Public Sub UnsubscribeConnectionLost(handler As Func(Of Task)) Implements IScaleProcessor.UnsubscribeConnectionLost
        _connectionlostHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeConnectionEstablished(handler As Func(Of Task)) Implements IScaleProcessor.SubscribeConnectionEstablished
        _connectionestablishedHandlers.Add(handler)
    End Sub

    Public Sub UnsubscribeConnectionEstablished(handler As Func(Of Task)) Implements IScaleProcessor.UnsubscribeConnectionEstablished
        _connectionestablishedHandlers.Remove(handler)
    End Sub

    Public Sub SubscribeScaleAlarm(handler As Func(Of Task)) Implements IScaleProcessor.SubscribeScaleAlarm
        _scalealarmHandlers.Add(handler)
    End Sub

    Public Sub UnsubscribeScaleAlarm(handler As Func(Of Task)) Implements IScaleProcessor.UnsubscribeScaleAlarm
        _scalealarmHandlers.Remove(handler)
    End Sub
End Class

