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

        var result = await new RabbitMqReadinessCheck(rabbitService.Object, new MessageConfiguration { Connection = "amqp://guest:guest@127.0.0.1:1" })
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

        var result = await new RabbitMqReadinessCheck(rabbitService.Object, new MessageConfiguration { Connection = "amqp://guest:guest@127.0.0.1:1" })
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("RabbitMQ channel is not open", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_IsHealthy_WhenChannelIsNotOpenedYet_AndBrokerIsReachable()
    {
        // A service that publishes lazily never opens the channel at start: the broker is probed instead (I01-WP16).
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var rabbitService = new Mock<IRabbitMqService>();
            rabbitService.SetupGet(s => s.RabbitMqChannel).Throws(new InvalidOperationException("Channel has not been initialized."));

            var result = await new RabbitMqReadinessCheck(rabbitService.Object, new MessageConfiguration { Connection = $"amqp://guest:guest@127.0.0.1:{port}" })
                .CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Contains("channel opens on first use", result.Description);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CheckHealthAsync_IsUnhealthy_WhenChannelIsNotOpenedYet_AndBrokerIsNotReachable()
    {
        // Take a free port and release it, so nothing listens there.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.SetupGet(s => s.RabbitMqChannel).Throws(new InvalidOperationException("Channel has not been initialized."));

        var result = await new RabbitMqReadinessCheck(rabbitService.Object, new MessageConfiguration { Connection = $"amqp://guest:guest@127.0.0.1:{port}" })
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("broker is not reachable", result.Description);
    }
}
