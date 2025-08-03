Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports System.Drawing
Imports System.IO
Imports System.IO.Ports
Imports System.Timers
Imports Scalemon.Common
Public Class SignalBus
    Implements ISignalBus, IDisposable

    Private ReadOnly _buttonpressedHandlers As New List(Of Func(Of Task))()

    Private _serialPort As SerialPort
    Private _portName As String = "COM1"
    Private _baudRate As Integer = 9600
    Private _reconnectIntervalMs As Integer = 5000
    Private _timer As Timer
    Private _isConnected As Boolean = False
    Private disposedValue As Boolean
    Private ReadOnly _config As IConfiguration
    Private ReadOnly _logger As ILogger(Of SignalBus)
    Public Sub New(config As IConfiguration, logger As ILogger(Of SignalBus))
        _config = config
        _logger = logger
    End Sub

    Public Property PortName As String Implements ISignalBus.PortName
        Get
            Return _portName
        End Get
        Set(value As String)
            _portName = value
        End Set
    End Property

    Public Property BaudRate As Integer Implements ISignalBus.BaudRate
        Get
            Return _baudRate
        End Get
        Set(value As Integer)
            _baudRate = value
        End Set
    End Property

    Public Property ReconnectIntervalMs As Integer Implements ISignalBus.ReconnectIntervalMs
        Get
            Return _reconnectIntervalMs
        End Get
        Set(value As Integer)
            _reconnectIntervalMs = value
        End Set
    End Property


    Public Event ConnectionEstablished() Implements ISignalBus.ConnectionEstablished
    Public Event ConnectionLost() Implements ISignalBus.ConnectionLost





    Public Sub Start() Implements ISignalBus.Start
        _portName = _config("PLCSettings:PortName")
        _baudRate = _config("PLCSettings:BaudRate")
        _reconnectIntervalMs = _config("PLCSettings:ReconnectIntervalMs")
        _serialPort = New SerialPort()
        With _serialPort
            .PortName = _portName
            .BaudRate = _baudRate
            .Parity = Parity.None
            .DataBits = 8
            .StopBits = StopBits.One
            .ReadTimeout = 500
        End With

        AddHandler _serialPort.DataReceived, AddressOf OnDataReceived
        TryOpenPort()
        _timer = New Timer(ReconnectIntervalMs)
        AddHandler _timer.Elapsed, Sub(sender, e) If Not _isConnected Then TryOpenPort()
        _timer.Start()
    End Sub

    Public Sub [Stop]() Implements ISignalBus.Stop
        RemoveHandler _serialPort.DataReceived, AddressOf OnDataReceived
        If _serialPort IsNot Nothing AndAlso _serialPort.IsOpen Then
            _serialPort.Close()
        End If
        _timer?.Stop()
    End Sub

    Private Sub TryOpenPort()
        Try
            If Not _serialPort.IsOpen Then _serialPort.Open()
            If Not _isConnected Then
                _isConnected = True
                RaiseEvent ConnectionEstablished()
            End If
        Catch
            If _isConnected Then
                _isConnected = False
                RaiseEvent ConnectionLost()
            End If
        End Try
    End Sub

    Private Async Sub OnDataReceived(sender As Object, e As SerialDataReceivedEventArgs)
        Try
            Dim data As Integer = _serialPort.ReadByte()
            If data = &H20 Then ' сигнал кнопки
                Await RaiseAllAsync(_buttonpressedHandlers)
            End If
        Catch ex As Exception
            ' возможная потеря соединения
            _logger.LogError(ex, "Ошибка при опросе весов")
        End Try
    End Sub

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' 1) Останавливаем логику Stop()
                Me.Stop()
                ' 2) Освобождаем SerialPort
                If _serialPort IsNot Nothing Then
                    _serialPort.Dispose()
                    _serialPort = Nothing
                End If
                ' 3) Освобождаем Timer
                If _timer IsNot Nothing Then
                    _timer.Dispose()
                    _timer = Nothing
                End If
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

    Public Sub SubscribeButtonPressed(handler As Func(Of Task)) Implements ISignalBus.SubscribeButtonPressed
        _buttonpressedHandlers.Add(handler)
    End Sub

    Public Sub UnsubscribeButtonPressed(handler As Func(Of Task)) Implements ISignalBus.UnsubscribeButtonPressed
        _buttonpressedHandlers.Remove(handler)
    End Sub

    Public Async Function SendAsync(cmd As ArduinoSignalCode) As Task Implements ISignalBus.SendAsync
        If _serialPort.IsOpen Then
            Await Task.Run(Sub()
                               _serialPort.Write(New Byte() {CByte(cmd)}, 0, 1)
                           End Sub)
        End If
    End Function
End Class


