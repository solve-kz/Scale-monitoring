using Microsoft.Extensions.Logging;
using Moq;
using Scalemon.Common;
using Scalemon.SerialLink;

namespace Scalemon.CSTests;

public class ScaleCommandProcessorTests
{
    private readonly Mock<IScaleDriver> _driver = new();
    private readonly Mock<ILogger<ScaleProcessor>> _logger = new();

    [Fact]
    public async Task ResetToZero_ReturnsExplicitTerminalRejection()
    {
        var expected = ScaleCommandResult.Rejected(0x15, "Установка нуля невозможна");
        _driver.Setup(driver => driver.SetToZero(500)).Returns(expected);
        using var processor = CreateProcessor();

        var actual = await processor.ResetToZeroAsync(500);

        Assert.Equal(expected, actual);
        _driver.Verify(driver => driver.SetToZero(500), Times.Once);
    }

    [Fact]
    public async Task SetTare_ForwardsMeasuredResidualAndTimeout()
    {
        var expected = ScaleCommandResult.Success(0x12, "OK");
        _driver.Setup(driver => driver.SetTare(0.08m, 700)).Returns(expected);
        using var processor = CreateProcessor();

        var actual = await processor.SetTareAsync(0.08m, 700);

        Assert.Equal(expected, actual);
        _driver.Verify(driver => driver.SetTare(0.08m, 700), Times.Once);
    }

    [Fact]
    public async Task TareCurrentWeight_PreservesTransportFailure()
    {
        var expected = ScaleCommandResult.TransportFailure("Тайм-аут");
        _driver.Setup(driver => driver.TareCurrentWeight(500)).Returns(expected);
        using var processor = CreateProcessor();

        var actual = await processor.TareCurrentWeightAsync(500);

        Assert.True(actual.IsTransportError);
        Assert.False(actual.Succeeded);
        _driver.Verify(driver => driver.TareCurrentWeight(500), Times.Once);
    }

    private ScaleProcessor CreateProcessor() =>
        new(_logger.Object, _driver.Object, "COM1", 100);
}
