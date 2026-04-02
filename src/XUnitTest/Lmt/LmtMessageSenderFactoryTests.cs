using SeliseBlocks.LMT.Client;

namespace XUnitTest.Lmt;

public class LmtMessageSenderFactoryTests
{
    [Fact]
    public void Create_ShouldReturnRabbitMqSender_ForAmqpConnection()
    {
        var options = new LmtOptions
        {
            ServiceId = "test-svc",
            ConnectionString = "amqp://guest:guest@localhost:5672"
        };

        var sender = LmtMessageSenderFactory.Create(options);

        try
        {
            Assert.IsType<LmtRabbitMqSender>(sender);
        }
        finally
        {
            // Dispose in background to avoid hang on connection retry
            Task.Run(() => { try { sender.Dispose(); } catch { } });
        }
    }

    [Fact]
    public void Create_ShouldReturnServiceBusSender_ForNonAmqpConnection()
    {
        var options = new LmtOptions
        {
            ServiceId = "test-svc",
            ConnectionString = "Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=abc"
        };

        var sender = LmtMessageSenderFactory.Create(options);

        try
        {
            Assert.IsType<LmtServiceBusSender>(sender);
        }
        finally
        {
            Task.Run(() => { try { sender.Dispose(); } catch { } });
        }
    }

    [Fact]
    public void Create_ShouldReturnServiceBusSender_ForEmptyConnection()
    {
        var options = new LmtOptions
        {
            ServiceId = "test-svc",
            ConnectionString = ""
        };

        var sender = LmtMessageSenderFactory.Create(options);

        try
        {
            Assert.IsType<LmtServiceBusSender>(sender);
        }
        finally
        {
            Task.Run(() => { try { sender.Dispose(); } catch { } });
        }
    }
}
