using Scalemon.Common.Updates;

namespace Scalemon.UpdateTests;

public sealed class UpdatePolicyTests
{
    [Theory]
    [InlineData(21, false)] [InlineData(22, true)] [InlineData(0, true)] [InlineData(4, true)] [InlineData(5, false)]
    public void NightWindowCrossesMidnight(int hour, bool expected)
        => Assert.Equal(expected, UpdatePolicy.InWindow(new(), new DateTime(2026, 9, 6, hour, 0, 0)));

    [Fact]
    public void DisconnectionAloneDoesNotAuthorizeStopping()
    {
        var now = DateTimeOffset.UtcNow;
        var safe = new ReadinessSnapshot("1.0.0", "process1", now, false, now.AddMinutes(-11), 0, 0, true, false, false, true, "");
        Assert.True(UpdatePolicy.Safe(safe, now));
        Assert.False(UpdatePolicy.Safe(safe with { Connected = true }, now));
        Assert.False(UpdatePolicy.Safe(safe with { DisconnectedSince = null }, now));
        Assert.False(UpdatePolicy.Safe(safe with { DisconnectedSince = now.AddMinutes(-9) }, now));
        Assert.False(UpdatePolicy.Safe(safe with { ObservedAt = now.AddSeconds(-16) }, now));
        Assert.False(UpdatePolicy.Safe(safe with { ObservedAt = now.AddSeconds(1) }, now));
        Assert.False(UpdatePolicy.Safe(safe with { PendingWrites = 1 }, now));
        Assert.False(UpdatePolicy.Safe(safe with { ActiveOperations = 1 }, now));
        Assert.False(UpdatePolicy.Safe(safe with { DatabaseAvailable = false }, now));
        Assert.False(UpdatePolicy.Safe(safe with { Healthy = false }, now));
    }
    [Theory]
    [InlineData("1.0.0-rc.9", "1.0.0-rc.10")]
    [InlineData("1.0.0-rc.10", "1.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    public void SemVerOrderingIsNumeric(string before, string after)
        => Assert.True(ReleaseVersion.Parse(before).CompareTo(ReleaseVersion.Parse(after)) < 0);
    [Theory]
    [InlineData("../1.0.0")] [InlineData("1.0.0/../2.0.0")] [InlineData("1.0.0-01")] [InlineData("01.0.0")]
    public void VersionCannotContainPaths(string value) => Assert.Throws<InvalidDataException>(() => ReleaseVersion.Parse(value));
    [Theory]
    [InlineData(UpdateStage.Stopped)] [InlineData(UpdateStage.Switching)] [InlineData(UpdateStage.Validating)] [InlineData(UpdateStage.RollingBack)]
    public void InterruptedSwitchRequiresRestoration(UpdateStage stage) => Assert.True(UpdateRecoveryPolicy.MayRestore(stage));
    [Fact]
    public void AcceptedReleaseAndPreStopPreparationMustNotTriggerAutomaticRollback()
    {
        Assert.False(UpdateRecoveryPolicy.MayRestore(UpdateStage.Accepted));
        Assert.False(UpdateRecoveryPolicy.MayRestore(UpdateStage.Prepared));
        Assert.True(UpdateRecoveryPolicy.MayCancel(UpdateStage.Prepared));
        Assert.False(UpdateRecoveryPolicy.MayCancel(UpdateStage.Switching));
    }
}
