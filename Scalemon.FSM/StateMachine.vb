Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging


Public Class StateMachine

    Implements IStateMachine

    Public Enum ScaleSystemState

        Idle
        Complited

        WeightSmallest
        WeightSmall

        ScaleAlarm
        Unstuble

    End Enum

    ' ������ ��������� ������������ ����� �����������:
    Private ReadOnly _config As IConfiguration
    Private ReadOnly _logger As ILogger(Of StateMachine)
    Private _hystweight As Decimal
    Private _minweight As Decimal
    Private _currentWeight As Decimal
    Private _currentState As Scalemon.Common.ScaleState


    Public ReadOnly Property LastWeight As Decimal Implements IStateMachine.LastWeight
        Get
            Return _currentWeight
        End Get
    End Property

    Public Sub New(config As IConfiguration, logger As ILogger(Of StateMachine))
        _config = config
        _logger = logger
        _hystweight = CDec(_config("ScaleSettings:HystWeight"))
        _minweight = CDec(_config("ScaleSettings:MinWeight"))
        _currentState = Common.ScaleState.Weighing
    End Sub

    Public Event StateChanged(newState As Common.ScaleState) Implements IStateMachine.StateChanged

    Public Sub HandleWeight(raw As Decimal) Implements IStateMachine.HandleWeight
        _currentWeight = raw
        ' ���������� ��������� ����
        Select Case raw
            Case < 0
                ' ����� �� ����
                _currentState = Common.ScaleState.ResetToZero
                RaiseEvent StateChanged(_currentState)
            Case 0
                ' ������ ���������� � �����������
                _currentState = Common.ScaleState.Idle
                RaiseEvent StateChanged(_currentState)
            Case <= _hystweight
                ' ����� �� ����
                _currentState = Common.ScaleState.ResetToZero
                RaiseEvent StateChanged(_currentState)
            Case <= _minweight
                ' ������ � ����������� � �������
                _currentState = Common.ScaleState.ComplitedSmall
                RaiseEvent StateChanged(_currentState)
            Case Else
                ' ���� ��� �������������� �������� ��������� - ������ � ����������� � �������
                ' ���� � ������������� ������� ���������� - ������ � �����������, ������ � ���� ������
        End Select


    End Sub




    Public Sub HandleArduinoConnectionLost() Implements IStateMachine.HandleArduinoConnectionLost
        _logger.LogError("������� ����� � �������")
    End Sub



    Public Sub HandleArduinoConnectionEstablished() Implements IStateMachine.HandleArduinoConnectionEstablished
        _logger.LogWarning("������������� ����� � �������")
    End Sub

    Public Sub HandleButtonPressed() Implements IStateMachine.HandleButtonPressed
        Throw New NotImplementedException()
    End Sub
End Class
