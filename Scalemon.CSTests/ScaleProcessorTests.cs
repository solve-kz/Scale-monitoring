using Microsoft.Extensions.Logging;
using Moq;
using Scalemon.Common;
using Scalemon.SerialLink;
using FluentAssertions;

namespace Scalemon.CSTests;

public class ScaleProcessorTests
{
    private readonly Mock<IScaleDriver> _mockDriver;
    private readonly Mock<ILogger<ScaleProcessor>> _mockLogger;

    public ScaleProcessorTests()
    {
        _mockDriver = new Mock<IScaleDriver>();
        _mockLogger = new Mock<ILogger<ScaleProcessor>>();
    }

    [Fact]
    public async Task Should_Trigger_WeightReceived_When_StableReached()
    {
        // Arrange
        decimal testWeight = 123.4m;
        _mockDriver.Setup(d => d.IsConnected ).Returns(true);
        _mockDriver.Setup(d => d.Stable).Returns(true);
        _mockDriver.Setup(d => d.Weight).Returns(testWeight);
        _mockDriver.Setup(d => d.LastResponseNum).Returns(0);

        var processor = new ScaleProcessor(
            _mockLogger.Object,
            _mockDriver.Object,
            portName: "COM1",
            stableThreshold: 1,
            unstableThreshold: 2,
            pollingIntervalMs: 10);

        bool eventFired = false;
        processor.SubscribeWeightReceived(async weight =>
        {
            eventFired = true;
            weight.Should().Be(testWeight);
            await Task.CompletedTask;
        });

        // Act
        processor.Start();
        await Task.Delay(30); // подождать цикл опроса
        processor.Stop();

        // Assert
        Assert.True(eventFired, "должно было произойти событие WeightReceived");
    }

    [Fact]
    public async Task Should_Trigger_Unstable_When_UnstableRepeated()
    {
        // Arrange
        _mockDriver.Setup(d => d.IsConnected).Returns(true);
        _mockDriver.Setup(d => d.Stable).Returns(false);
        _mockDriver.Setup(d => d.LastResponseNum).Returns(0);

        var processor = new ScaleProcessor(
            _mockLogger.Object,
            _mockDriver.Object,
            "COM1", 3, 1, 10);

        bool unstableCalled = false;
        processor.SubscribeUnstable(async () =>
        {
            unstableCalled = true;
            await Task.CompletedTask;
        });

        // Act
        processor.Start();
        await Task.Delay(30); // дать время для 1 итерации
        processor.Stop();

        // Assert
        Assert.True(unstableCalled, "событие Unstable должно было быть вызвано");
    }

    [Fact]
    public void Should_CloseConnection_OnStop()
    {
        // Arrange
        _mockDriver.Setup(d => d.IsConnected).Returns(true);

        var processor = new ScaleProcessor(
            _mockLogger.Object,
            _mockDriver.Object,
            "COM1", 1, 1, 50);

        // Act
        processor.Start();
        Thread.Sleep(20); // короткий запуск
        processor.Stop();

        // Assert
        _mockDriver.Verify(d => d.CloseConnection(), Times.Once);
    }

    [Fact]
    public async Task ResetToZero_Should_Throw_When_ResponseHasError()
    {
        // Arrange
        _mockDriver.Setup(d => d.SetToZero());
        _mockDriver.Setup(d => d.LastResponseNum).Returns(2); // ошибка

        var processor = new ScaleProcessor(
            _mockLogger.Object,
            _mockDriver.Object,
            "COM1", 1, 1, 10);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ResetToZeroAsync());
    }
}
