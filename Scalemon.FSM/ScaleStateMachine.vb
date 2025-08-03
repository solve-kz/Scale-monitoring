Imports System.Threading
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.Logging
Imports Scalemon.Common

Imports Stateless

Public Class ScaleStateMachine

    Implements IScaleStateMachine

    Private ReadOnly _fsm As StateMachine(Of ScalesState, Trigger)
    Private ReadOnly _weightReceivedTrigger As StateMachine(Of ScalesState, Trigger).TriggerWithParameters(Of Decimal)
    Private ReadOnly _onResetToZero As Func(Of Task)
    Private ReadOnly _onError As Func(Of Task)
    Private ReadOnly _logger As ILogger(Of ScaleStateMachine)
    Private ReadOnly _semaphore As New SemaphoreSlim(1, 1)
    Private ReadOnly _hystWeight As Decimal
    Private ReadOnly _minWeight As Decimal

    Private _zeroFlag As Boolean
    Private _errorFlag As Boolean
    Private _isInvalidWeight As Boolean
    Private _lastRaw As Decimal
    Private _semaphoreTime As Integer

    Public Sub New(config As IConfiguration,
                       logger As ILogger(Of ScaleStateMachine),
                       onConnected As Func(Of Task),
                       onDisconnected As Func(Of Task),
                       onUnstable As Func(Of Task),
                       onResetToZero As Func(Of Task),
                       onZeroState As Func(Of Task),
                       onInvalidWeight As Func(Of Task),
                       onError As Func(Of Task),
                       onResetAlarm As Func(Of Task),
                       onRecord As Func(Of Decimal, Task))

        ' Читаем настройки
        _hystWeight = CDec(config("ScaleSettings:HystWeight"))
        _minWeight = CDec(config("ScaleSettings:MinWeight"))
        _semaphoreTime = Integer.Parse(config("ScaleSettings:SemaphoreTime"))

        ' Создаём FSM
        _fsm = New StateMachine(Of ScalesState, Trigger)(ScalesState.Disconnected)
        ' "Оборачиваем" обычный enum-триггер WeightReceived в параметризованный:
        _weightReceivedTrigger = _fsm.SetTriggerParameters(Of Decimal)(Trigger.WeightReceived)
        _onResetToZero = onResetToZero
        _onError = onError
        _logger = logger

        ' 1) Подключение/отключение
        _fsm.Configure(ScalesState.Disconnected) _
                .Permit(Trigger.ScaleConnected, ScalesState.Connected) _
                .Permit(Trigger.DatabaseFailure, ScalesState.DatabaseError)

        _fsm.Configure(ScalesState.Connected) _
                .Permit(Trigger.ScaleDisconnected, ScalesState.Disconnected) _
                .Permit(Trigger.ScaleAlarm, ScalesState.ScaleError) _
                .Permit(Trigger.DatabaseFailure, ScalesState.DatabaseError) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .PermitDynamic(Of Decimal)(_weightReceivedTrigger, AddressOf DetermineStateFromWeight) _
                .OnEntryAsync(Async Function()
                                  Await onConnected()
                              End Function) _
                .OnExitAsync(Async Function()
                                 Await onDisconnected()
                             End Function)

        ' 2) Нестабильное состояние
        _fsm.Configure(ScalesState.Unstable) _
                .SubstateOf(ScalesState.Connected) _
                .OnEntryFromAsync(Trigger.ArduinoButtonPressed, Async Function()
                                                                    If _errorFlag Then
                                                                        _errorFlag = False
                                                                        Await onResetAlarm()
                                                                    End If
                                                                End Function) _
                .OnEntryAsync(Async Function()
                                  If _isInvalidWeight Then
                                      ' Сбрасываем сигнализацию, если была ошибка взвешивания
                                      _isInvalidWeight = False
                                      Await onResetAlarm()
                                  End If
                                  Await onUnstable()
                              End Function)

        ' 3) Стабилизированное состояние — суперкласс для весовых подкатегорий
        _fsm.Configure(ScalesState.Stabilized) _
                .SubstateOf(ScalesState.Connected) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Ignore(Trigger.WeightReceived)

        ' 4) Категории внутри Stabilized
        _fsm.Configure(ScalesState.NegativeWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntryAsync(AddressOf HandleResetAttemptAsync)

        _fsm.Configure(ScalesState.ZeroWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntryAsync(Async Function()
                                  _zeroFlag = True
                                  Await onZeroState()
                              End Function)

        _fsm.Configure(ScalesState.LightWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntryAsync(AddressOf HandleResetAttemptAsync)

        _fsm.Configure(ScalesState.InvalidWeight) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntryAsync(Async Function()
                                  _isInvalidWeight = True
                                  Await onInvalidWeight()
                              End Function)


        _fsm.Configure(ScalesState.Recorded) _
                .SubstateOf(ScalesState.Stabilized) _
                .OnEntryAsync(Async Function()
                                  _zeroFlag = False
                                  Await onRecord(_lastRaw)
                              End Function)

        _fsm.Configure(ScalesState.ErrorAfterWeighing) _
                .SubstateOf(ScalesState.Stabilized) _
                .Permit(Trigger.ArduinoButtonPressed, ScalesState.Unstable) _
                .OnEntryAsync(Async Function()
                                  _errorFlag = True
                                  Await onInvalidWeight()
                              End Function)

        ' 5) Аппаратная ошибка весов
        _fsm.Configure(ScalesState.ScaleError) _
                .SubstateOf(ScalesState.Connected) _
                .Permit(Trigger.ScaleUnstable, ScalesState.Unstable) _
                .Permit(Trigger.ArduinoButtonPressed, ScalesState.Unstable) _
                .PermitDynamic(Of Decimal)(_weightReceivedTrigger, AddressOf DetermineStateFromWeight) _
                .OnEntryAsync(Async Function()
                                  Await onError()
                              End Function)

        ' 6) Ошибка базы данных
        _fsm.Configure(ScalesState.DatabaseError) _
                .OnEntryAsync(Async Function()
                                  Await onError() ' Зажигаем красную лампу
                              End Function) _
                .Permit(Trigger.DatabaseRestored, ScalesState.Unstable) ' Выход из ошибки при восстановлении БД
    End Sub

    Private Function DetermineStateFromWeight(raw As Decimal) As ScalesState
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
            If _zeroFlag Then
                Return ScalesState.Recorded
            Else
                Return ScalesState.ErrorAfterWeighing
            End If
        End If
    End Function

    Public Async Function OnScaleConnectedAsync() As Task Implements IScaleStateMachine.OnScaleConnectedAsync
        If Not Await _semaphore.WaitAsync(2000) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение 2 секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            Await _fsm.FireAsync(Trigger.ScaleConnected)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Public Async Function OnScaleDisconnectedAsync() As Task Implements IScaleStateMachine.OnScaleDisconnectedAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            Await _fsm.FireAsync(Trigger.ScaleDisconnected)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Private Async Function HandleResetAttemptAsync() As Task
        Dim resetException As Exception = Nothing
        Try
            ' Пытаемся выполнить асинхронную операцию
            Await _onResetToZero()
        Catch ex As Exception
            ' В блоке Catch только сохраняем исключение
            resetException = ex
        End Try
        ' Проверяем, была ли ошибка, уже ПОСЛЕ блока Catch
        If resetException IsNot Nothing Then
            _logger.LogError(resetException, "Автоматический сброс веса не удался.")
            Await _onError() ' Включаем красную лампу
            Await _fsm.FireAsync(Trigger.ScaleAlarm) ' Переходим в состояние ошибки FSM
        End If
    End Function

    Public Async Function OnScaleUnstableAsync() As Task Implements IScaleStateMachine.OnScaleUnstableAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            Await _fsm.FireAsync(Trigger.ScaleUnstable)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Public Async Function OnScaleAlarmAsync() As Task Implements IScaleStateMachine.OnScaleAlarmAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            Await _fsm.FireAsync(Trigger.ScaleAlarm)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Public Async Function OnWeightReceivedAsync(raw As Decimal) As Task Implements IScaleStateMachine.OnWeightReceivedAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            Await _fsm.FireAsync(_weightReceivedTrigger, raw)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Public Async Function OnButtonPressedAsync() As Task Implements IScaleStateMachine.OnButtonPressedAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            Await _fsm.FireAsync(Trigger.ArduinoButtonPressed)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Public Async Function OnDatabaseFailedAsync(ex As Exception) As Task Implements IScaleStateMachine.OnDatabaseFailedAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            _logger.LogError(ex, "Получен сигнал о сбое в базе данных.")
            Await _fsm.FireAsync(Trigger.DatabaseFailure)
        Finally
            _semaphore.Release()
        End Try
    End Function

    Public Async Function OnDatabaseRestoredAsync() As Task Implements IScaleStateMachine.OnDatabaseRestoredAsync
        If Not Await _semaphore.WaitAsync(_semaphoreTime) Then
            _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.")
            Return
        End If
        Try
            _logger.LogInformation("Получен сигнал о восстановлении базы данных.")
            Await _fsm.FireAsync(Trigger.DatabaseRestored)
        Finally
            _semaphore.Release()
        End Try
    End Function
End Class
