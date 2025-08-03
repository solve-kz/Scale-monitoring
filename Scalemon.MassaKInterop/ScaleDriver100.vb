
Public Class ScaleDriver100
    Implements IScaleDriver

    Private _scale As MassaKDriver100.Scales
    Private _response As Long


    Public Sub New()
        _scale = New MassaKDriver100.Scales()
        _response = 0
    End Sub

    Public Property PortConnection As String Implements IScaleDriver.PortConnection
        Get
            Return _scale.Connection
        End Get
        Set(value As String)
            _scale.Connection = value
        End Set
    End Property

    Public ReadOnly Property Weight As Decimal Implements IScaleDriver.Weight
        Get
            Return CDec(_scale.Weight) / 100D
        End Get
    End Property

    Public ReadOnly Property Stable As Boolean Implements IScaleDriver.Stable
        Get
            Return _scale.Stable = 1
        End Get
    End Property

    Public ReadOnly Property LastResponseText As String Implements IScaleDriver.LastResponseText
        Get
            Dim _message As String
            Select Case _response
                Case 0
                    _message = "Ошибок нет"
                Case 1
                    _message = "Связь с весами не установлена"
                Case 2
                    _message = "Ошибка обмена данных с весами"
                Case 3
                    _message = "Весы не готовы к передаче данных"
                Case 4
                    _message = "Параметр не поддерживается весами"
                Case 5
                    _message = "Установка параметра невозможна"
                Case 7
                    _message = "Невозможно выполнить команду или команда не поддерживается"
                Case 8
                    _message = "Нагрузка на весовом устройстве превышает НПВ"
                Case 9
                    _message = "Весовое устройство не в режиме взвешивания"
                Case 10
                    _message = "Ошибка входных данных"
                Case 11
                    _message = "Ошибка сохранения данных"
                Case 16
                    _message = "Интерфейс Wi-Fi не поддерживается"
                Case 17
                    _message = "Интерфейс Ethernet не поддерживается"
                Case 21
                    _message = "Установка нуля невозможна из-за наличия нагрузки на платформе"
                Case 23
                    _message = "Нет связи с модулем взвешивания"
                Case 24
                    _message = "Установлена нагрузка на платформу при включении весового устройства"
                Case 25
                    _message = "Весовое устройство неисправно"
                Case Else
                    _message = "Неописанная в документации ошибка"
            End Select
            Return _message

        End Get
    End Property

    Public ReadOnly Property LastResponseNum As Long Implements IScaleDriver.LastResponseNum
        Get
            Return _response
        End Get
    End Property

    Public ReadOnly Property isConnected As Boolean Implements IScaleDriver.isConnected
        Get
            Select Case _response
                Case 1, 2, 3, 23
                    Return False
                Case Else
                    Return True
            End Select
        End Get
    End Property

    Public ReadOnly Property isScaleAlarm As Boolean Implements IScaleDriver.isScaleAlarm
        Get
            Select Case _response
                Case 0, 1, 2, 3, 23
                    Return False
                Case Else
                    Return True
            End Select
        End Get
    End Property

    Public Sub OpenConnection() Implements IScaleDriver.OpenConnection
        Try
            _response = _scale.OpenConnection()
        Catch ex As Exception
            Throw New InvalidOperationException($"Ошибка драйвера OpenConnection(): {LastResponseText}", ex)
        End Try

    End Sub

    Public Sub CloseConnection() Implements IScaleDriver.CloseConnection
        Try
            _response = _scale.CloseConnection()
        Catch ex As Exception
            Throw New InvalidOperationException($"Ошибка драйвера CloseConnection(): {LastResponseText}", ex)
        End Try

    End Sub

    Public Sub SetToZero() Implements IScaleDriver.SetToZero
        Try
            _response = _scale.SetToZero()
        Catch ex As Exception
            Throw New InvalidOperationException($"Ошибка драйвера SetToZero(): {LastResponseText}", ex)
        End Try

    End Sub

    Public Sub ReadWeight() Implements IScaleDriver.ReadWeight
        Try
            _response = _scale.ReadWeight()
        Catch ex As Exception
            Throw New InvalidOperationException($"Ошибка драйвера ReadWeight(): {LastResponseText}", ex)
        End Try

    End Sub
End Class


