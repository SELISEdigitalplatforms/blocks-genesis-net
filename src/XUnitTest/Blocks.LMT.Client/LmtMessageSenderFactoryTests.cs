using Xunit;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class LmtMessageSenderFactoryTests
    {
        private readonly LmtOptions _serviceBuilOptions;
        private readonly LmtOptions _rabbitMqOptions;

        public LmtMessageSenderFactoryTests()
        {
            _serviceBuilOptions = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test=="
            };

            _rabbitMqOptions = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };
        }

        [Fact]
        public void Create_ThrowsWhenOptionsIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => LmtMessageSenderFactory.Create(null!));
        }

        [Fact]
        public void Create_ReturnsLmtServiceBusSenderForServiceBusConnectionString()
        {
            // Act
            var sender = LmtMessageSenderFactory.Create(_serviceBuilOptions);

            // Assert
            Assert.NotNull(sender);
            Assert.IsType<LmtServiceBusSender>(sender);
            sender.Dispose();
        }

        [Fact]
        public void Create_ReturnsLmtRabbitMqSenderForRabbitMqConnectionString()
        {
            // Act
            var sender = LmtMessageSenderFactory.Create(_rabbitMqOptions);

            // Assert
            Assert.NotNull(sender);
            Assert.IsType<LmtRabbitMqSender>(sender);
            sender.Dispose();
        }

        [Fact]
        public void CreateShared_ThrowsWhenOptionsIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => LmtMessageSenderFactory.CreateShared(null!));
        }

        [Fact]
        public void CreateShared_ReturnsILmtMessageSender()
        {
            // Act
            var sender = LmtMessageSenderFactory.CreateShared(_serviceBuilOptions);

            // Assert
            Assert.NotNull(sender);
            Assert.IsAssignableFrom<ILmtMessageSender>(sender);
            sender.Dispose();
        }

        [Fact]
        public void CreateShared_ReturnsSameSenderForIdenticalOptions()
        {
            // Arrange
            var options1 = new LmtOptions
            {
                ServiceId = "service1",
                ConnectionString = "amqp://guest:guest@localhost:5672/",
                MaxRetries = 3,
                MaxFailedBatches = 100
            };

            var options2 = new LmtOptions
            {
                ServiceId = "service1",
                ConnectionString = "amqp://guest:guest@localhost:5672/",
                MaxRetries = 3,
                MaxFailedBatches = 100
            };

            // Act
            var sender1 = LmtMessageSenderFactory.CreateShared(options1);
            var sender2 = LmtMessageSenderFactory.CreateShared(options2);

            // Assert - Should be same instance (reference counted)
            Assert.Same(sender1, sender2);

            sender1.Dispose();
            sender2.Dispose();
        }

        [Fact]
        public void CreateShared_ReturnsDifferentSendersForDifferentServiceIds()
        {
            // Arrange
            var options1 = new LmtOptions
            {
                ServiceId = "service1",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            var options2 = new LmtOptions
            {
                ServiceId = "service2",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            // Act
            var sender1 = LmtMessageSenderFactory.CreateShared(options1);
            var sender2 = LmtMessageSenderFactory.CreateShared(options2);

            // Assert
            Assert.NotSame(sender1, sender2);

            sender1.Dispose();
            sender2.Dispose();
        }

        [Fact]
        public void CreateShared_ReturnsDifferentSendersForDifferentConnectionStrings()
        {
            // Arrange
            var options1 = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            var options2 = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqps://guest:guest@secure.rabbitmq.com:5671/"
            };

            // Act
            var sender1 = LmtMessageSenderFactory.CreateShared(options1);
            var sender2 = LmtMessageSenderFactory.CreateShared(options2);

            // Assert
            Assert.NotSame(sender1, sender2);

            sender1.Dispose();
            sender2.Dispose();
        }
    }
}
