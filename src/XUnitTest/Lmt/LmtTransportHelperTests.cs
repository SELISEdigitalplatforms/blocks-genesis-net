using SeliseBlocks.LMT.Client;

namespace XUnitTest.Lmt;

public class LmtTransportHelperTests
{
    [Fact]
    public void IsRabbitMq_ShouldReturnTrue_ForAmqpScheme()
    {
        Assert.True(LmtTransportHelper.IsRabbitMq("amqp://guest:guest@localhost:5672"));
    }

    [Fact]
    public void IsRabbitMq_ShouldReturnTrue_ForAmqpsScheme()
    {
        Assert.True(LmtTransportHelper.IsRabbitMq("amqps://guest:guest@rabbitmq.example.com:5671"));
    }

    [Fact]
    public void IsRabbitMq_ShouldReturnTrue_CaseInsensitive()
    {
        Assert.True(LmtTransportHelper.IsRabbitMq("AMQP://localhost"));
        Assert.True(LmtTransportHelper.IsRabbitMq("AMQPS://localhost"));
    }

    [Fact]
    public void IsRabbitMq_ShouldReturnFalse_ForServiceBusEndpoint()
    {
        Assert.False(LmtTransportHelper.IsRabbitMq("Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=abc"));
    }

    [Fact]
    public void IsRabbitMq_ShouldReturnFalse_ForHttpScheme()
    {
        Assert.False(LmtTransportHelper.IsRabbitMq("https://example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsRabbitMq_ShouldReturnFalse_ForNullOrWhitespace(string? connection)
    {
        Assert.False(LmtTransportHelper.IsRabbitMq(connection));
    }

    [Fact]
    public void IsRabbitMq_ShouldReturnFalse_ForInvalidUri()
    {
        Assert.False(LmtTransportHelper.IsRabbitMq("not a valid uri at all!!!"));
    }

    [Fact]
    public void IsRabbitMq_ShouldReturnFalse_ForRelativeUri()
    {
        Assert.False(LmtTransportHelper.IsRabbitMq("/relative/path"));
    }
}
