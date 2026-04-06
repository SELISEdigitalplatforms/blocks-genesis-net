using Xunit;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class LmtTransportHelperTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsRabbitMq_ReturnsFalseForNullOrEmptyConnectionString(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsRabbitMq(connectionString);

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData("amqp://guest:guest@localhost:5672/")]
        [InlineData("amqps://guest:guest@secure.rabbitmq.com:5671/")]
        [InlineData("AMQP://USER:PASS@HOST:5672/")] // Case-insensitive
        public void IsRabbitMq_ReturnsTrueForValidRabbitMqUri(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsRabbitMq(connectionString);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData("http://localhost:5672")]
        [InlineData("https://localhost:5672")]
        [InlineData("Endpoint=sb://test.servicebus.windows.net/")]
        [InlineData("not-a-uri-at-all")]
        public void IsRabbitMq_ReturnsFalseForNonRabbitMqUris(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsRabbitMq(connectionString);

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsValidConnectionString_ReturnsFalseForNullOrEmpty(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsValidConnectionString(connectionString);

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData("amqp://guest:guest@localhost:5672/")]
        [InlineData("amqps://guest:guest@secure.rabbitmq.com:5671/")]
        public void IsValidConnectionString_ReturnsTrueForValidRabbitMqConnectionString(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsValidConnectionString(connectionString);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test==")]
        [InlineData("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyValue=test==")]
        public void IsValidConnectionString_ReturnsTrueForValidServiceBusConnectionString(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsValidConnectionString(connectionString);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData("Endpoint=http://localhost")] // Missing SharedAccessKey
        [InlineData("SharedAccessKey=test==")]     // Missing Endpoint
        [InlineData("http://localhost:5672")]      // Not RabbitMQ, not Service Bus
        [InlineData("invalid-connection-string")]
        public void IsValidConnectionString_ReturnsFalseForInvalidConnectionString(string connectionString)
        {
            // Act
            var result = LmtTransportHelper.IsValidConnectionString(connectionString);

            // Assert
            Assert.False(result);
        }
    }
}
