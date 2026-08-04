using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMqServiceLiveConnectionTests
{
    [Fact]
    public async Task CreateConnectionAsync_ShouldEstablishConnectionAndChannel_AgainstLocalBroker()
    {
        if (!await IsLocalBrokerAvailable())
        {
            return;
        }

        var config = new MessageConfiguration
        {
            Connection = "amqp://<username>:<password>@127.0.0.1:5672"
        };

        await using var service = new RabbitMqService(new Mock<ILogger<RabbitMqService>>().Object, config);

        await service.CreateConnectionAsync();

        Assert.NotNull(service.RabbitMqChannel);
        Assert.True(service.RabbitMqChannel.IsOpen);
    }

    private static async Task<bool> IsLocalBrokerAvailable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", 5672);
            var timeout = Task.Delay(TimeSpan.FromSeconds(2));
            var completed = await Task.WhenAny(connectTask, timeout);
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
