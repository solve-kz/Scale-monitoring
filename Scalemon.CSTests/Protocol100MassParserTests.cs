using Scalemon.SerialLink;

namespace Scalemon.CSTests;

public class Protocol100MassParserTests
{
    [Fact]
    public void ParsesAllMassResponseFieldsWithoutCrcValidation()
    {
        byte[] body =
        {
            0x24,
            0x80, 0x06, 0x00, 0x00,
            0x02,
            0x01,
            0x01,
            0x00,
            0x14, 0x00, 0x00, 0x00
        };

        var parsed = Protocol100MassParser.TryParse(body, out var reading, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(reading);
        Assert.Equal(16.64m, reading.WeightKg);
        Assert.Equal(0.01m, reading.DivisionKg);
        Assert.True(reading.IsStable);
        Assert.True(reading.IsNet);
        Assert.False(reading.IsTerminalZero);
        Assert.Equal(0.20m, reading.TareKg);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    public void RejectsUnexpectedMassResponseLength(int length)
    {
        var body = new byte[length];
        body[0] = 0x24;

        var parsed = Protocol100MassParser.TryParse(body, out var reading, out _);

        Assert.False(parsed);
        Assert.Null(reading);
    }

    [Fact]
    public void RejectsUnknownDivision()
    {
        var body = new byte[9];
        body[0] = 0x24;
        body[5] = 0x7F;

        var parsed = Protocol100MassParser.TryParse(body, out var reading, out _);

        Assert.False(parsed);
        Assert.Null(reading);
    }
}
