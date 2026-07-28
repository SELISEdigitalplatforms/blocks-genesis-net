using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMqServiceLiveConnectionTests
{
    [Fact]
    public async Task CreateConnectionAsync_ShouldEstablishConnectionAndChannel_AgainstLocalBroker()
    {
        var config = new MessageConfiguration
        {
            Connection = "amqp://guest:guest@127.0.0.1:5672"
        };

        await using var service = new RabbitMqService(new Mock<ILogger<RabbitMqService>>().Object, config);

        await service.CreateConnectionAsync();

        Assert.NotNull(service.RabbitMqChannel);
        Assert.True(service.RabbitMqChannel.IsOpen);
    }
}
