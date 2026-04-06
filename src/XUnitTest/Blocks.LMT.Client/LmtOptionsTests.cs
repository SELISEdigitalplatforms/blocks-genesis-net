using Xunit;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class LmtOptionsTests
    {
        [Fact]
        public void Validate_ThrowsWhenServiceIdIsEmpty()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "",
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test"
            };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }

        [Fact]
        public void Validate_ThrowsWhenConnectionStringIsEmpty()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = ""
            };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }

        [Fact]
        public void Validate_ThrowsWhenConnectionStringIsInvalid()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "invalid-connection-string"
            };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }

        [Fact]
        public void Validate_SucceedsWithValidServiceBusConnectionString()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test"
            };

            // Act & Assert
            options.Validate(); // Should not throw
        }

        [Fact]
        public void Validate_SucceedsWithValidRabbitMqConnectionString()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            // Act & Assert
            options.Validate(); // Should not throw
        }

        [Theory]
        [InlineData(0, 100)] // Zero should default to 100
        [InlineData(50, 50)]
        [InlineData(500, 500)]
        public void LogBatchSize_ValidatesAndSetsCorrectly(int input, int expected)
        {
            // Arrange & Act
            var options = new LmtOptions { LogBatchSize = input };

            // Assert
            Assert.Equal(expected, options.LogBatchSize);
        }

        [Theory]
        [InlineData(-5, 1)]  // Negative values should be capped at max of 10
        [InlineData(0, 0)]   // Zero is valid
        [InlineData(5, 5)]
        [InlineData(15, 10)] // Values > 10 should be capped
        public void MaxRetries_CapsAtMaximumValue(int input, int expected)
        {
            // Arrange & Act
            var options = new LmtOptions { MaxRetries = input };

            // Assert
            Assert.Equal(expected, options.MaxRetries);
        }
    }
}
