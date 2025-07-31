Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports System.Drawing
Imports System.IO
Imports System.IO.Ports
Imports System.Timers
Imports Scalemon.Common
Public Class SignalBus
    Implements ISignalBus
    Private _serialPort As SerialPort
    Private _portName As String = "COM1"
    Private _baudRate As Integer = 9600
    Private _reconnectIntervalMs As Integer = 5000
    Private _timer As Timer
    Private _isConnected As Boolean = False

    Private ReadOnly _config As IConfiguration
    Private ReadOnly _logger As ILogger(Of SignalBus)
    Public Sub New(config As IConfiguration, logger As ILogger(Of SignalBus))
        _config = config
        _logger = logger
        _serialPort.Parity = Parity.None
        _serialPort.DataBits = 8
        _serialPort.StopBits = StopBits.One
        _serialPort.ReadTimeout = 500

    End Sub


    Public Property PortName As String Implements ISignalBus.PortName
        Get
            Return _portName
        End Get
        Set(value As String)
            _portName = value
            _config("PLCSettigs:PortName") = value
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
        Public Event ButtonPressed() Implements ISignalBus.ButtonPressed

    Public Sub Send(cmd As ArduinoSignalCode) Implements ISignalBus.Send
        If _serialPort.IsOpen Then
            _serialPort.Write(New Byte() {CByte(cmd)}, 0, 1)
        End If
    End Sub

    Public Sub Start() Implements ISignalBus.Start
        _portName = _config("PLCSettigs:PortName")
        _baudRate = _config("PLCSettigs:BaudRate")
        _reconnectIntervalMs = _config("PLCSettings:ReconnectIntervalMs")
        _serialPort = New SerialPort(_portName, _baudRate)
        AddHandler _serialPort.DataReceived, AddressOf OnDataReceived
            TryOpenPort()

            _timer = New Timer(ReconnectIntervalMs)
            AddHandler _timer.Elapsed, Sub(sender, e) If Not _isConnected Then TryOpenPort()
            _timer.Start()
        End Sub

        Public Sub [Stop]() Implements ISignalBus.Stop
            If _serialPort IsNot Nothing Then
                RemoveHandler _serialPort.DataReceived, AddressOf OnDataReceived
                If _serialPort.IsOpen Then _serialPort.Close()
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

        Private Sub OnDataReceived(sender As Object, e As SerialDataReceivedEventArgs)
            Try
                Dim data As Integer = _serialPort.ReadByte()
                If data = &H20 Then ' сигнал кнопки
                    RaiseEvent ButtonPressed()
                End If
            Catch
                ' возможная потеря соединения
            End Try
        End Sub

    End Class


