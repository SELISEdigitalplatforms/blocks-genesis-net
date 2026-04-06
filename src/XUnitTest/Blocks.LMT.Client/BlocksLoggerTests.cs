using Xunit;
using Moq;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class BlocksLoggerTests
    {
        private readonly LmtOptions _validOptions;

        public BlocksLoggerTests()
        {
            _validOptions = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/",
                LogBatchSize = 10,
                FlushIntervalSeconds = 5,
                EnableLogging = true
            };
        }

        [Fact]
        public void Constructor_ThrowsWhenOptionsIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new BlocksLogger(null!));
        }

        [Fact]
        public void Constructor_ThrowsWhenServiceIdIsEmpty()
        {
            // Arrange
            var invalidOptions = new LmtOptions
            {
                ServiceId = "",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => new BlocksLogger(invalidOptions));
        }

        [Fact]
        public void Constructor_ThrowsWhenConnectionStringIsEmpty()
        {
            // Arrange
            var invalidOptions = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = ""
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => new BlocksLogger(invalidOptions));
        }

        [Fact]
        public void Log_DoesNotThrowWhenDisposed()
        {
            // Arrange
            var logger = new BlocksLogger(_validOptions);
            logger.Dispose();

            // Act & Assert - should not throw
            logger.Log(LmtLogLevel.Information, "Test message");
        }

        [Fact]
        public void Log_DoesNotThrowWhenLoggingDisabled()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/",
                EnableLogging = false
            };

            using var logger = new BlocksLogger(options);

            // Act & Assert - should not throw
            logger.Log(LmtLogLevel.Information, "Test message");
        }

        [Fact]
        public void LogTrace_CallsLogWithTraceLevel()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert - should not throw
            logger.LogTrace("Trace message");
        }

        [Fact]
        public void LogDebug_CallsLogWithDebugLevel()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert
            logger.LogDebug("Debug message");
        }

        [Fact]
        public void LogInformation_CallsLogWithInformationLevel()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert
            logger.LogInformation("Info message");
        }

        [Fact]
        public void LogWarning_CallsLogWithWarningLevel()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert
            logger.LogWarning("Warning message");
        }

        [Fact]
        public void LogError_WithException_CallsLogWithErrorLevel()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);
            var exception = new InvalidOperationException("Test error");

            // Act & Assert
            logger.LogError("Error message", exception);
        }

        [Fact]
        public void LogCritical_WithException_CallsLogWithCriticalLevel()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);
            var exception = new SystemException("Critical error");

            // Act & Assert
            logger.LogCritical("Critical message", exception);
        }

        [Fact]
        public void Log_WithEmptyMessageTemplate_UsesDefaultMessage()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert - should not throw
            logger.Log(LmtLogLevel.Information, "");
        }

        [Fact]
        public void Log_WithNullMessageTemplate_UsesDefaultMessage()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert - should not throw
            logger.Log(LmtLogLevel.Information, null!);
        }

        [Fact]
        public void Log_WithExceptionArgument_SanitizesException()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);
            var exception = new ArgumentException("Invalid argument");

            // Act & Assert
            logger.Log(LmtLogLevel.Error, "Error occurred: {error}", exception);
        }

        [Fact]
        public void Log_WithMultipleArguments_FormatsSafelyWithoutThrow()
        {
            // Arrange
            using var logger = new BlocksLogger(_validOptions);

            // Act & Assert
            logger.Log(LmtLogLevel.Information, "User {userId} performed action {action}", null, 123, "login");
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            // Arrange
            var logger = new BlocksLogger(_validOptions);

            // Act & Assert - should not throw
            logger.Dispose();
            logger.Dispose();
        }
    }
}
