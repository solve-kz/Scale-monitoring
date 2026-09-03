using Scalemon.Common;

namespace Scalemon.CSTests;

public sealed class SlaughterModeStateTests
{
    [Fact]
    public async Task ModeMustBeConfirmedAgainAfterConnectionLoss()
    {
        var state = new SlaughterModeState();

        Assert.False(state.IsKnown);
        Assert.Throws<InvalidOperationException>(() => _ = state.Current);
        var firstConfirmation = state.WaitUntilKnownAsync();
        Assert.False(firstConfirmation.IsCompleted);

        state.Set(SlaughterMode.Sanitary);

        Assert.Equal(SlaughterMode.Sanitary, await firstConfirmation);
        Assert.True(state.IsKnown);
        Assert.Equal(SlaughterMode.Sanitary, state.Current);

        state.Reset();
        var reconfirmation = state.WaitUntilKnownAsync();
        Assert.False(state.IsKnown);
        Assert.False(reconfirmation.IsCompleted);

        state.Set(SlaughterMode.General);

        Assert.Equal(SlaughterMode.General, await reconfirmation);
        Assert.Equal(SlaughterMode.General, state.Current);
    }
}
