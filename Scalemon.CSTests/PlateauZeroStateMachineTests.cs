using Microsoft.Extensions.Logging;
using Moq;
using Scalemon.Common;
using Scalemon.FSM;
using static Scalemon.Common.Enums;

namespace Scalemon.CSTests;

public class PlateauZeroStateMachineTests
{
    [Fact]
    public async Task RecordsMedianImmediatelyAndAllowsSameMassOnlyAfterUnload()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 16.64m, 16.66m, 16.65m);

        Assert.Equal(new[] { 16.65m }, records);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);

        await FeedAsync(fsm, 16.65m, 16.65m, 5.60m, 5.60m, 5.60m);
        Assert.Single(records);

        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 16.65m, 16.65m, 16.65m);

        Assert.Equal(new[] { 16.65m, 16.65m }, records);
    }

    [Fact]
    public async Task AwaitUnloadAcceptsTwoConsecutiveFreshLowReadingsWithoutStableFlag()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);
        await FeedSampleAsync(fsm, 0.24m, isStable: false);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);

        await FeedSampleAsync(fsm, 0.20m, isStable: false);
        await FeedAsync(fsm, 8.10m, 8.10m, 8.10m);

        Assert.Equal(new[] { 7.50m, 8.10m }, records);
        Assert.Empty(commands);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public async Task AwaitUnloadRequiresLowFreshReadingsToBeConsecutive()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);
        await FeedSampleAsync(fsm, 0.20m, isStable: false);
        await FeedSampleAsync(fsm, 0.40m, isStable: false);
        await FeedSampleAsync(fsm, 0.18m, isStable: false);

        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);

        await FeedSampleAsync(fsm, 0.16m, isStable: false);
        await FeedAsync(fsm, 8.10m, 8.10m, 8.10m);

        Assert.Equal(new[] { 7.50m, 8.10m }, records);
    }

    [Fact]
    public async Task StableLoadImmediatelyStartsPlateauAfterStartup()
    {
        var records = new List<decimal>();
        var fsm = CreateFsm(records, new());
        fsm.SetConnection(true);

        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public async Task StableLoadImmediatelyStartsPlateauAfterConnectionRecovery()
    {
        var records = new List<decimal>();
        var fsm = CreateFsm(records, new());
        fsm.SetConnection(true);
        fsm.SetConnection(false);
        fsm.SetConnection(true);

        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public async Task ResidualAfterUnloadDoesNotChangeAlreadyRecordedPlateau()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 16.64m, 16.66m, 16.65m);
        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);

        Assert.Equal(new[] { 16.65m }, records);
        Assert.Equal(ResidualCorrectionCommand.SetTare, Assert.Single(commands).Command);
    }

    [Fact]
    public async Task ExplicitZeroRejectionLatchesErrorAfterConfiguredAttempts()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.ZeroCommandMaxAttempts = 3;
        var fsm = CreateFsm(records, commands, _ =>
            ScaleCommandResult.Rejected(0x15, "ZERO rejected"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(
            fsm,
            Enumerable.Repeat(0.08m, settings.UnloadStableSamples * 3).ToArray());

        Assert.Equal(3, commands.Count);
        Assert.All(commands, request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command));
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
        Assert.True(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task ZeroTimeoutDoesNotSendTareAndDisconnects()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(
            records,
            commands,
            _ => ScaleCommandResult.TransportFailure("timeout"));
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);

        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
        Assert.Equal(FsmState.Disconnected, fsm.CurrentState);
    }

    [Fact]
    public async Task ZeroAndTareBoundariesAreInclusiveAndHigherResidualRequiresCleaning()
    {
        var zeroCommands = new List<ResidualCorrectionRequest>();
        var zeroFsm = CreateFsm(new(), zeroCommands);
        await ConnectAndArmAsync(zeroFsm);
        await FeedAsync(zeroFsm, 0.10m, 0.10m, 0.10m);
        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(zeroCommands).Command);

        var tareCommands = new List<ResidualCorrectionRequest>();
        var tareFsm = CreateFsm(new(), tareCommands);
        await ConnectAndArmAsync(tareFsm);
        await FeedAsync(tareFsm, 0.30m, 0.30m, 0.30m);
        Assert.Equal(ResidualCorrectionCommand.SetTare, Assert.Single(tareCommands).Command);

        var cleaningCommands = new List<ResidualCorrectionRequest>();
        var cleaningFsm = CreateFsm(new(), cleaningCommands);
        await ConnectAndArmAsync(cleaningFsm);
        await FeedAsync(cleaningFsm, 0.31m, 0.31m, 0.31m);
        Assert.Empty(cleaningCommands);
        Assert.Equal(FsmState.InvalidWeightState, cleaningFsm.CurrentState);
        Assert.True(cleaningFsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task ResidualAboveTareMaximumAfterRecordedLoadLatchesError()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);
        await FeedAsync(fsm, 0.31m, 0.31m, 0.31m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Empty(commands);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
        Assert.True(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task NegativeResidualUsesOnlyZero()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(new(), commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.05m, -0.05m, -0.05m);

        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
    }

    [Fact]
    public async Task LargeNegativeResidualLatchesRedWithoutBlockingNextLoad()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.31m, -0.31m, -0.31m);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Empty(commands);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
        Assert.True(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task RemovingPreviouslyTaredWaterTriggersNegativeRecoverySequence()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(new(), commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, -0.20m, -0.20m, -0.20m);

        Assert.Collection(
            commands,
            request => Assert.Equal(ResidualCorrectionCommand.SetTare, request.Command),
            request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command));
    }

    [Fact]
    public async Task SmallStableResidualReappearingAfterZeroTriggersAnotherCorrection()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(new(), commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);
        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command));
    }

    [Fact]
    public async Task AutomaticZeroApplicationLimitLatchesErrorBeforeNextCommand()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.MaxAutomaticZeroApplications = 2;
        var fsm = CreateFsm(new(), commands, settings: settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);
        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);
        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);

        Assert.Equal(2, commands.Count);
        Assert.True(fsm.HasLatchedProcessError);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
    }

    [Fact]
    public async Task AutomaticTareApplicationLimitLatchesErrorBeforeNextCommand()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.MaxAutomaticTareApplications = 2;
        var fsm = CreateFsm(new(), commands, settings: settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);

        Assert.Equal(2, commands.Count);
        Assert.True(fsm.HasLatchedProcessError);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
    }

    [Fact]
    public async Task TareRejectionUsesConfiguredAttemptLimit()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.TareCommandMaxAttempts = 2;
        var fsm = CreateFsm(
            new(),
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "TARE rejected"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(
            fsm,
            Enumerable.Repeat(0.20m, settings.UnloadStableSamples * 2).ToArray());

        Assert.Equal(2, commands.Count);
        Assert.All(commands, request => Assert.Equal(ResidualCorrectionCommand.SetTare, request.Command));
        Assert.True(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task TareRejectionLatchesRedUntilStableZero()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.TareCommandMaxAttempts = 1;
        var fsm = CreateFsm(
            new(),
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "TARE rejected"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
        Assert.True(fsm.HasLatchedProcessError);
        Assert.Single(commands);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        Assert.Single(commands);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);

        await FeedZeroAsync(fsm);
        Assert.Equal(FsmState.IdleZero, fsm.CurrentState);
        Assert.False(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task NegativeZeroRejectionDoesNotSendTareOrBlockNextLoad()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.ZeroCommandMaxAttempts = 1;
        var fsm = CreateFsm(
            records,
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "ZERO rejected"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.02m, -0.02m, -0.02m);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
        Assert.True(fsm.HasLatchedProcessError);

        await FeedAsync(fsm, 7.68m, 7.68m, 7.68m);

        Assert.Equal(new[] { 7.68m }, records);
        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
        Assert.DoesNotContain(commands, request =>
            request.Command is ResidualCorrectionCommand.SetTare or
                ResidualCorrectionCommand.TareCurrentWeight);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public async Task AugustNegativeResidualTraceRecordsEveryLoadWithoutRepeatedCommands()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.ZeroCommandMaxAttempts = 1;
        var fsm = CreateFsm(
            records,
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "Установка >0< невозможна"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 7.68m, 7.68m, 7.68m);
        await FeedAsync(fsm, -0.02m, -0.02m, -0.02m);
        await FeedAsync(fsm, 7.22m, 7.22m, 7.22m);
        await FeedAsync(fsm, -0.02m, -0.02m, -0.02m);
        await FeedAsync(fsm, 8.10m, 8.10m, 8.10m);
        await FeedAsync(fsm, -0.02m, -0.02m, -0.02m);

        Assert.Equal(new[] { 7.68m, 7.22m, 8.10m }, records);
        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
    }

    [Fact]
    public async Task PositiveTareRejectionKeepsRecordingAndDoesNotRetrySameResidual()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.TareCommandMaxAttempts = 1;
        var fsm = CreateFsm(
            records,
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "TARE rejected"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);
        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Equal(ResidualCorrectionCommand.SetTare, Assert.Single(commands).Command);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
    }

    [Fact]
    public async Task ProductArrivingBeforeZeroConfirmationIsRecorded()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);
        await FeedAsync(fsm, 7.68m, 7.68m, 7.68m);

        Assert.Equal(new[] { 7.68m }, records);
        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public async Task LargeResidualLatchesRedButDoesNotBlockNextLoad()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.31m, 0.31m, 0.31m);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Empty(commands);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
        Assert.True(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task TransportFailureAllowsRecordingAfterConnectionRecovery()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(
            records,
            commands,
            _ => ScaleCommandResult.TransportFailure("timeout"));
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.02m, -0.02m, -0.02m);
        Assert.Equal(FsmState.Disconnected, fsm.CurrentState);

        fsm.SetConnection(true);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public async Task FreshStableResidualAllowsNextCorrectionAttempt()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(
            new(),
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "rejected"));
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.02m, -0.02m, -0.02m);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);
        await FeedAsync(fsm, -0.06m, -0.06m, -0.06m);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command));
    }

    [Fact]
    public async Task DisabledNonBlockingCorrectionKeepsLoadBlockedWhileErrorIsLatched()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var settings = CreateSettings();
        settings.EnableNonBlockingCorrection = false;
        settings.ZeroCommandMaxAttempts = 1;
        var fsm = CreateFsm(
            records,
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "ZERO rejected"),
            settings);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.05m, -0.05m, -0.05m);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Empty(records);
        Assert.Equal(ResidualCorrectionCommand.SetZero, Assert.Single(commands).Command);
        Assert.True(fsm.HasLatchedProcessError);

        await FeedZeroAsync(fsm);
        await FeedAsync(fsm, 7.50m, 7.50m, 7.50m);

        Assert.Equal(new[] { 7.50m }, records);
        Assert.False(fsm.HasLatchedProcessError);
    }

    [Fact]
    public async Task DatabaseFailureDoesNotRetryRecordedCycle()
    {
        var attempts = 0;
        var logger = new Mock<ILogger>().Object;
        var fsm = new PlateauZeroStateMachine(
            CreateSettings(),
            logger,
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("DB down");
            },
            (_, _) => Task.FromResult(ScaleCommandResult.Success(0, "OK")));
        fsm.SetConnection(true);
        await FeedZeroAsync(fsm);

        await FeedAsync(fsm, 16.64m, 16.65m, 16.66m, 16.65m, 16.65m, 16.65m);

        Assert.Equal(1, attempts);
        Assert.Equal(FsmState.AwaitUnload, fsm.CurrentState);
    }

    [Fact]
    public void SettingsRejectTareBandThatOverlapsProductWeight()
    {
        var settings = CreateSettings();
        settings.MinWeight = 0.30;

        Assert.Throws<InvalidOperationException>(settings.ValidateWeighing);
    }

    [Fact]
    public void CorrectionProtectionSettingsHaveExpectedDefaults()
    {
        var settings = new SystemSettings();

        Assert.Equal(10, settings.ZeroCommandMaxAttempts);
        Assert.Equal(5, settings.TareCommandMaxAttempts);
        Assert.Equal(3, settings.MaxAutomaticZeroApplications);
        Assert.Equal(2, settings.MaxAutomaticTareApplications);
    }

    [Fact]
    public void SettingsRejectNonPositiveCorrectionLimits()
    {
        var settings = CreateSettings();
        settings.ZeroCommandMaxAttempts = 0;
        Assert.Throws<InvalidOperationException>(settings.ValidateWeighing);

        settings = CreateSettings();
        settings.TareCommandMaxAttempts = 0;
        Assert.Throws<InvalidOperationException>(settings.ValidateWeighing);

        settings = CreateSettings();
        settings.MaxAutomaticZeroApplications = 0;
        Assert.Throws<InvalidOperationException>(settings.ValidateWeighing);

        settings = CreateSettings();
        settings.MaxAutomaticTareApplications = 0;
        Assert.Throws<InvalidOperationException>(settings.ValidateWeighing);
    }

    private static PlateauZeroStateMachine CreateFsm(
        List<decimal> records,
        List<ResidualCorrectionRequest> commands,
        Func<ResidualCorrectionRequest, ScaleCommandResult>? commandResult = null,
        SystemSettings? settings = null)
    {
        commandResult ??= _ => ScaleCommandResult.Success(0, "OK");
        return new PlateauZeroStateMachine(
            settings ?? CreateSettings(),
            new Mock<ILogger>().Object,
            weight =>
            {
                records.Add(weight);
                return Task.CompletedTask;
            },
            (request, _) =>
            {
                commands.Add(request);
                return Task.FromResult(commandResult(request));
            });
    }

    private static SystemSettings CreateSettings() => new()
    {
        MinWeight = 1.0,
        ZeroResidualMaxKg = 0.10m,
        TareAllowancePercent = 200m,
        PlateauStableSamples = 3,
        PlateauWindowKg = 0.04m,
        UnloadStableSamples = 3,
        CommandTimeoutMs = 500,
        ZeroCommandMaxAttempts = 10,
        TareCommandMaxAttempts = 5,
        MaxAutomaticZeroApplications = 3,
        MaxAutomaticTareApplications = 2
    };

    private static async Task ConnectAndArmAsync(PlateauZeroStateMachine fsm)
    {
        fsm.SetConnection(true);
        await FeedZeroAsync(fsm);
        Assert.Equal(FsmState.IdleZero, fsm.CurrentState);
    }

    private static Task FeedZeroAsync(PlateauZeroStateMachine fsm) =>
        FeedAsync(fsm, new[] { 0m, 0m, 0m }, terminalZero: true);

    private static Task FeedAsync(PlateauZeroStateMachine fsm, params decimal[] weights) =>
        FeedAsync(fsm, weights, terminalZero: false);

    private static Task FeedSampleAsync(
        PlateauZeroStateMachine fsm,
        decimal weight,
        bool isStable,
        bool hasFreshMeasurement = true) =>
        fsm.OnSampleAsync(new ScaleDataPoint(
            weight,
            isStable,
            isConnected: true,
            isAlarm: false,
            hasFreshMeasurement: hasFreshMeasurement,
            divisionKg: 0.01m));

    private static async Task FeedAsync(
        PlateauZeroStateMachine fsm,
        IEnumerable<decimal> weights,
        bool terminalZero)
    {
        foreach (var weight in weights)
        {
            await fsm.OnSampleAsync(new ScaleDataPoint(
                weight,
                isStable: true,
                isConnected: true,
                isAlarm: false,
                hasFreshMeasurement: true,
                divisionKg: 0.01m,
                isTerminalZero: terminalZero));
        }
    }
}
