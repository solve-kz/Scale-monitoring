Imports System.Threading
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports Scalemon.MassaKInterop

Public Class ScaleProcessor
    Implements IScaleProcessor

    Private ReadOnly _driver As IScaleDriver = New ScaleDriver100
    Private ReadOnly _logger As ILogger(Of ScaleProcessor)
    Private ReadOnly _config As IConfiguration
    Private WithEvents Timer As New Timers.Timer
    Private _stableCount As Integer = 0
    Private _unstableCount As Integer = 0


    Public Sub New(config As IConfiguration, logger As ILogger(Of ScaleProcessor))
        _config = config
        _logger = logger
    End Sub

    Public Event WeightReceived(raw As Decimal) Implements IScaleProcessor.WeightReceived
    Public Event Unstable() Implements IScaleProcessor.Unstable
    Public Event ConnectionLost() Implements IScaleProcessor.ConnectionLost
    Public Event ConnectionEstablished() Implements IScaleProcessor.ConnectionEstablished
    Public Event ScaleAlarm() Implements IScaleProcessor.ScaleAlarm


    Public Sub Start() Implements IScaleProcessor.Start
        ' Реализация запуска драйвера весов
        _driver.PortConnection = _config("PLCSettings:PortName")
        _driver.OpenConnection()
        If _driver.isConnected Then
            RaiseEvent ConnectionEstablished()
        Else
            RaiseEvent ConnectionLost()
        End If
        Timer.Interval = Integer.Parse(_config("ScaleSettings:PollingIntervalMs"))
        Timer.Start()

    End Sub

    Private Sub OnTimerElapsed(sender As Object, e As Timers.ElapsedEventArgs) Handles Timer.Elapsed
        Try
            If Not _driver.isConnected Then
                _driver.OpenConnection()
                If _driver.isConnected Then RaiseEvent ConnectionEstablished()
            End If
            If _driver.isConnected Then
                _driver.ReadWeight()
                Select Case _driver.LastResponseNum
                    Case 0
                        If _driver.Stable Then
                            _stableCount += 1
                            _unstableCount = 0
                            If _stableCount = _config("ScaleSettings:StableThreshold") Then
                                RaiseEvent WeightReceived(_driver.Weight)
                            End If
                        Else
                            _unstableCount += 1
                            _stableCount = 0
                            If _unstableCount = _config("ScaleSettings:UnstableThreshold") Then
                                RaiseEvent Unstable()
                            End If
                        End If
                    Case 1
                        ' Обработка ошибки 
                        _driver.CloseConnection()
                        RaiseEvent ConnectionLost()
                    Case Else
                        RaiseEvent ScaleAlarm()
                End Select
            End If
        Catch ex As Exception
            _logger.LogError(ex, "Ошибка при опросе весов")
            _driver.CloseConnection()
            RaiseEvent ConnectionLost()
        End Try
    End Sub



    Public Sub [Stop]() Implements IScaleProcessor.Stop
        ' Реализация остановки драйвера весов
        Timer.Stop()
    End Sub
    Public Sub ResetToZero() Implements IScaleProcessor.ResetToZero
        ' Реализация сброса веса к нулю
        _driver.SetToZero()
        If _driver.LastResponseNum > 0 Then
            ' Обработка ошибки сброса на ноль
        End If
    End Sub


End Class


