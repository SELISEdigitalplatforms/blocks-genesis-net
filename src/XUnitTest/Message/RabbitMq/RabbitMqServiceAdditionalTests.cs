using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMqServiceAdditionalTests
{
    [Fact]
    public async Task CreateConnectionAsync_ShouldSwallowException_WhenConnectionUriIsInvalid()
    {
        // The ConnectionFactory will throw (either at `new Uri(...)` construction
        // or later inside CreateConnectionAsync when the host is unreachable) —
        // RabbitMqService.CreateConnectionAsync must catch and log, not propagate.
        var logger = new Mock<ILogger<RabbitMqService>>();
        var config = new MessageConfiguration
        {
            // Valid URI format but unreachable host — ConnectionFactory.CreateConnectionAsync
            // will throw and the service is expected to swallow it.
            Connection = "amqp://guest:guest@nonexistent-host-for-unit-test.local:5672",
            RabbitMqConfiguration = new RabbitMqConfiguration()
        };

        var service = new RabbitMqService(logger.Object, config);

        var exception = await Record.ExceptionAsync(() => service.CreateConnectionAsync());

        Assert.Null(exception);
        // After a failed connection attempt, the channel is still not initialized.
        Assert.Throws<InvalidOperationException>(() => _ = service.RabbitMqChannel);
    }

    [Fact]
    public async Task CreateConnectionAsync_ShouldSwallowException_WhenConnectionStringIsMalformed()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var config = new MessageConfiguration
        {
            Connection = "not-a-valid-uri",
            RabbitMqConfiguration = new RabbitMqConfiguration()
        };

        var service = new RabbitMqService(logger.Object, config);

        var exception = await Record.ExceptionAsync(() => service.CreateConnectionAsync());

        Assert.Null(exception);
    }
}
