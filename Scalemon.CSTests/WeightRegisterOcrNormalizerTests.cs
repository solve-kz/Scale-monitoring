using Scalemon.WebApp.Data;

namespace Scalemon.CSTests;

public sealed class WeightRegisterOcrNormalizerTests
{
    [Theory]
    [InlineData("1468", 14.68)]
    [InlineData("786", 7.86)]
    [InlineData("930", 9.30)]
    [InlineData("80", 8.00)]
    [InlineData("6", 6.00)]
    [InlineData("10", 10.00)]
    public void NormalizeBody_FollowsRegisterRules(string text, decimal expected)
    {
        var result = WeightRegisterOcrNormalizer.NormalizeBody(text);

        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void NormalizeBody_TrailingDoubleZero_IsCorrectedAndMarkedSuspect()
    {
        var result = WeightRegisterOcrNormalizer.NormalizeBody("700");

        Assert.Equal(7.06m, result.Value);
        Assert.True(result.IsSuspect);
        Assert.Contains("700 → 706", result.Reason);
    }

    [Fact]
    public void NormalizeBody_OddHundredth_IsNotRoundedSilently()
    {
        var result = WeightRegisterOcrNormalizer.NormalizeBody("785");

        Assert.Equal(7.85m, result.Value);
        Assert.True(result.IsSuspect);
        Assert.Contains("0,02", result.Reason);
    }

    [Fact]
    public void NormalizeTotal_UsesHundredthsOfKilogram()
    {
        var result = WeightRegisterOcrNormalizer.NormalizeTotal("37060");

        Assert.Equal(370.60m, result.Value);
    }

    [Fact]
    public void NormalizeTotal_OddHundredth_IsMarkedSuspect()
    {
        var result = WeightRegisterOcrNormalizer.NormalizeTotal("37061");

        Assert.Equal(370.61m, result.Value);
        Assert.True(result.IsSuspect);
        Assert.Contains("0,02", result.Reason);
    }
}
