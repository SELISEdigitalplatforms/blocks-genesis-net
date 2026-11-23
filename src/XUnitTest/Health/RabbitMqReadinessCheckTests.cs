using Blocks.Genesis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using RabbitMQ.Client;

namespace XUnitTest.Health;

public class RabbitMqReadinessCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_IsHealthy_WhenChannelIsOpen()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.SetupGet(s => s.RabbitMqChannel).Returns(channel.Object);

        var result = await new RabbitMqReadinessCheck(rabbitService.Object)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_IsUnhealthy_WhenChannelIsClosed()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(false);
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.SetupGet(s => s.RabbitMqChannel).Returns(channel.Object);

        var result = await new RabbitMqReadinessCheck(rabbitService.Object)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("RabbitMQ channel is not open", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_IsUnhealthy_WhenChannelIsNotInitialized()
    {
        // RabbitMqService throws until the channel exists: the check reports "not ready" instead of failing.
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.SetupGet(s => s.RabbitMqChannel)
            .Throws(new InvalidOperationException("Channel has not been initialized."));

        var result = await new RabbitMqReadinessCheck(rabbitService.Object)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("RabbitMQ channel has not been initialized", result.Description);
    }
}
