Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports Scalemon.Common

Imports Stateless

Public Class ScaleStateMachine

    Implements IScaleStateMachine

    Private _zeroFlag As Boolean
    Private _errorFlag As Boolean
    Private _lastRaw As Decimal
    Private _isInvalidWeight As Boolean

    Private ReadOnly _fsm As StateMachine(Of ScalesState, Trigger)
    Private ReadOnly _weightReceivedTrigger As StateMachine(Of ScalesState, Trigger).TriggerWithParameters(Of Decimal)
    Private ReadOnly _hystWeight As Decimal
    Private ReadOnly _minWeight As Decimal



    Public Sub New(config As IConfiguration,
                       logger As ILogger(Of ScaleStateMachine),
                       onConnected As Action,
                       onDisconnected As Action,
                       onUnstable As Action,
                       onResetToZero As Action,
                       onZeroState As Action,
                       onInvalidWeight As Action,
                       onError As Action,
                       onResetAlarm As Action,
                       onRecord As Action(Of Decimal))

        ' Читаем настройки
        _hystWeight = CDec(config("ScaleSettings:HystWeight"))
        _minWeight = CDec(config("ScaleSettings:MinWeight"))

        ' Создаём FSM
        _fsm = New StateMachine(Of ScalesState, Trigger)(ScalesState.Disconnected)
        ' "Оборачиваем" обычный enum-триггер WeightReceived в параметризованный:
        _weightReceivedTrigger = _fsm.SetTriggerParameters(Of Decimal)(Trigger.WeightReceived)

        ' 1) Подключение/отключение
        _fsm.Configure(ScalesState.Disconnected) _
                .Permit(Trigger.ScaleConnected, ScalesState.Connected)

        _fsm.Configure(ScalesState.Connected) _
                .Permit(Trigger.ScaleDisconnected, ScalesState.Disconnected) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError) _
                .PermitDynamic(_weightReceivedTrigger, Function(raw As Decimal) ScalesState.Stabilized) _
                .OnEntry(Sub() onConnected()) _
                .OnExit(Sub() onDisconnected())

        ' 2) Нестабильное состояние
        _fsm.Configure(ScalesState.Unstable) _
                .SubstateOf(ScalesState.Connected) _
                .OnEntryFrom(Trigger.ArduinoButtonPressed, Sub() onResetAlarm()) _
                .OnEntry(Sub()
                             If _isInvalidWeight Then
                                 ' Сбрасываем сигнализацию, если была ошибка взвешивания
                                 _isInvalidWeight = False
                                 onResetAlarm()
                             End If
                             onUnstable()
                         End Sub) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)



        ' 3) Стабилизированное состояние — суперкласс для весовых подкатегорий
        _fsm.Configure(ScalesState.Stabilized) _
                .SubstateOf(ScalesState.Connected) _
                .PermitDynamic(Of Decimal)(_weightReceivedTrigger,
                                           Function(raw As Decimal) As ScalesState
                                               _lastRaw = raw
                                               If raw < 0D Then
                                                   Return ScalesState.NegativeWeight
                                               ElseIf raw = 0D Then
                                                   Return ScalesState.ZeroWeight
                                               ElseIf raw <= _hystWeight Then
                                                   Return ScalesState.LightWeight
                                               ElseIf raw <= _minWeight Then
                                                   Return ScalesState.InvalidWeight
                                               Else
                                                   Return ScalesState.ReadyToRecord
                                               End If
                                           End Function) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        ' 4) Категории внутри Stabilized
        _fsm.Configure(ScalesState.NegativeWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntry(Sub() onResetToZero()) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        _fsm.Configure(ScalesState.ZeroWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntry(Sub()
                             _zeroFlag = True
                             onZeroState()
                         End Sub) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        _fsm.Configure(ScalesState.LightWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntry(Sub() onResetToZero()) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        _fsm.Configure(ScalesState.InvalidWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntry(Sub()
                             _isInvalidWeight = True
                             onInvalidWeight()
                         End Sub) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        _fsm.Configure(ScalesState.ReadyToRecord) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntryFrom(_weightReceivedTrigger, Sub(raw)
                                                         If _zeroFlag Then
                                                             _zeroFlag = False
                                                             _fsm.Fire(Trigger.RecordSuccess)
                                                         Else
                                                             _fsm.Fire(Trigger.RecordFailure)
                                                         End If
                                                     End Sub) _
                .Permit(Trigger.RecordSuccess, ScalesState.Recorded) _
                .Permit(Trigger.RecordFailure, ScalesState.ErrorAfterWeighing) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        _fsm.Configure(ScalesState.Recorded) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntry(Sub() onRecord(_lastRaw)) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        _fsm.Configure(ScalesState.ErrorAfterWeighing) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntry(Sub()
                             _errorFlag = True
                             onInvalidWeight()
                         End Sub) _
                .Permit(Trigger.ArduinoButtonPressed, ScalesState.Unstable) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError)

        ' 5) Аппаратная ошибка весов
        _fsm.Configure(ScalesState.ScaleError) _
                .SubstateOf(ScalesState.Connected) _
                .OnEntry(Sub() onError()) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ArduinoButtonPressed, ScalesState.Unstable)

    End Sub
    Public Sub OnScaleConnected() Implements IScaleStateMachine.OnScaleConnected
        _fsm.Fire(Trigger.ScaleConnected)
    End Sub

    Public Sub OnScaleDisconnected() Implements IScaleStateMachine.OnScaleDisconnected
        _fsm.Fire(Trigger.ScaleDisconnected)
    End Sub

    Public Sub OnScaleStabilized() Implements IScaleStateMachine.OnScaleStabilized
        _fsm.Fire(Trigger.ScaleStabilized)
    End Sub

    Public Sub OnScaleUnstable() Implements IScaleStateMachine.OnScaleUnstable
        _fsm.Fire(Trigger.ScaleUnstable)
    End Sub

    Public Sub OnScaleAlarm() Implements IScaleStateMachine.OnScaleAlarm
        _fsm.Fire(Trigger.ScaleAlarm)
    End Sub

    Public Sub OnWeightReceived(raw As Decimal) Implements IScaleStateMachine.OnWeightReceived
        _fsm.Fire(_weightReceivedTrigger, raw)
    End Sub

    Public Sub OnButtonPressed() Implements IScaleStateMachine.OnButtonPressed
        _errorFlag = False
        _fsm.Fire(Trigger.ArduinoButtonPressed)
    End Sub

    Public Sub OnZeroState() Implements IScaleStateMachine.OnZeroState
        _fsm.Fire(Trigger.ReadinessToScale)
    End Sub

    Public Sub OnResetToZero() Implements IScaleStateMachine.OnResetToZero
        _fsm.Fire(Trigger.ResetToZero)
    End Sub


End Class
