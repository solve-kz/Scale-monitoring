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
    public async Task ExplicitZeroRejectionFallsBackToOneMeasuredTare()
    {
        var records = new List<decimal>();
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(records, commands, request =>
            request.Command == ResidualCorrectionCommand.SetZero
                ? ScaleCommandResult.Rejected(0x15, "ZERO rejected")
                : ScaleCommandResult.Success(0x12, "TARE accepted"));
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.08m, 0.08m, 0.08m);

        Assert.Collection(
            commands,
            request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command),
            request =>
            {
                Assert.Equal(ResidualCorrectionCommand.SetTare, request.Command);
                Assert.Equal(0.08m, request.WeightKg);
            });
        Assert.Equal(FsmState.Weighing, fsm.CurrentState);
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
    }

    [Fact]
    public async Task NegativeResidualUsesTareCurrentThenZero()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(new(), commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.05m, -0.05m, -0.05m);

        Assert.Collection(
            commands,
            request => Assert.Equal(ResidualCorrectionCommand.TareCurrentWeight, request.Command),
            request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command));
    }

    [Fact]
    public async Task LargeNegativeResidualRequiresCleaningWithoutCommands()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(new(), commands);
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, -0.31m, -0.31m, -0.31m);

        Assert.Empty(commands);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
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
            request => Assert.Equal(ResidualCorrectionCommand.TareCurrentWeight, request.Command),
            request => Assert.Equal(ResidualCorrectionCommand.SetZero, request.Command));
    }

    [Fact]
    public async Task TareRejectionLatchesRedUntilStableNormalReading()
    {
        var commands = new List<ResidualCorrectionRequest>();
        var fsm = CreateFsm(
            new(),
            commands,
            _ => ScaleCommandResult.Rejected(0x15, "TARE rejected"));
        await ConnectAndArmAsync(fsm);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);
        Assert.Single(commands);

        await FeedAsync(fsm, 0.20m, 0.20m, 0.20m);
        Assert.Single(commands);
        Assert.Equal(FsmState.InvalidWeightState, fsm.CurrentState);

        await FeedZeroAsync(fsm);
        Assert.Equal(FsmState.IdleZero, fsm.CurrentState);
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

    private static PlateauZeroStateMachine CreateFsm(
        List<decimal> records,
        List<ResidualCorrectionRequest> commands,
        Func<ResidualCorrectionRequest, ScaleCommandResult>? commandResult = null)
    {
        commandResult ??= _ => ScaleCommandResult.Success(0, "OK");
        return new PlateauZeroStateMachine(
            CreateSettings(),
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
        CommandTimeoutMs = 500
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
