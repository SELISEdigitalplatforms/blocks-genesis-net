using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class IntegrationTests
    {
        [Fact]
        public void FullLoggingPipeline_WorksWithServiceBusConfig()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLmtClient(opts =>
            {
                opts.ServiceId = "integration-test-service";
                opts.ConnectionString = "amqp://guest:guest@localhost:5672/";
                opts.LogBatchSize = 5;
                opts.FlushIntervalSeconds = 1;
            });

            var serviceProvider = services.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<IBlocksLogger>();

            // Act & Assert
            logger.LogInformation("Test message");
            logger.LogError("Error message", new Exception("Test exception"));
            logger.LogWarning("Warning message");
            logger.LogDebug("Debug message");
            logger.LogTrace("Trace message");

            // Cleanup
            (logger as IDisposable)?.Dispose();
        }

        [Fact]
        public void MultipleLoggerInstances_ShareSameSender()
        {
            // Arrange
            var options = new LmtOptions
            {
                ServiceId = "shared-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            // Act
            var sender1 = LmtMessageSenderFactory.CreateShared(options);
            var sender2 = LmtMessageSenderFactory.CreateShared(options);

            // Assert - Should be same instance due to reference counting
            Assert.Same(sender1, sender2);

            sender1.Dispose();
            sender2.Dispose();
        }

        [Fact]
        public void ConfigurationBinding_WorksCorrectly()
        {
            // Arrange
            var configBuilder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "Lmt:ServiceId", "config-test" },
                    { "Lmt:ConnectionString", "amqp://guest:guest@localhost:5672/" },
                    { "Lmt:LogBatchSize", "50" },
                    { "Lmt:TraceBatchSize", "500" },
                    { "Lmt:FlushIntervalSeconds", "10" },
                    { "Lmt:MaxRetries", "5" },
                    { "Lmt:MaxFailedBatches", "200" },
                    { "Lmt:EnableLogging", "true" },
                    { "Lmt:EnableTracing", "false" },
                    { "Lmt:XBlocksKey", "test-tenant" }
                });

            var services = new ServiceCollection();
            services.AddLmtClient(configBuilder.Build());
            var serviceProvider = services.BuildServiceProvider();

            // Act
            var options = serviceProvider.GetRequiredService<LmtOptions>();

            // Assert
            Assert.Equal("config-test", options.ServiceId);
            Assert.Equal("amqp://guest:guest@localhost:5672/", options.ConnectionString);
            Assert.Equal(50, options.LogBatchSize);
            Assert.Equal(500, options.TraceBatchSize);
            Assert.Equal(10, options.FlushIntervalSeconds);
            Assert.Equal(5, options.MaxRetries);
            Assert.Equal(200, options.MaxFailedBatches);
            Assert.True(options.EnableLogging);
            Assert.False(options.EnableTracing);
            Assert.Equal("test-tenant", options.XBlocksKey);
        }

        [Fact]
        public void LoggerDoesNotThrowWhenLoggingIsDisabled()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLmtClient(opts =>
            {
                opts.ServiceId = "disabled-test";
                opts.ConnectionString = "amqp://guest:guest@localhost:5672/";
                opts.EnableLogging = false;
            });

            var serviceProvider = services.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<IBlocksLogger>();

            // Act & Assert - should not throw
            logger.LogInformation("This should be ignored");
            logger.LogError("Error should also be ignored");

            (logger as IDisposable)?.Dispose();
        }

        [Fact]
        public void DisposingLoggerMultipleTimes_DoesNotThrow()
        {
            // Arrange
            var logger = new BlocksLogger(new LmtOptions
            {
                ServiceId = "dispose-test",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            });

            // Act & Assert
            logger.Dispose();
            logger.Dispose();
            logger.Dispose();
        }

        [Fact]
        public void InvalidConfiguration_ThrowsAtStartup()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                services.AddLmtClient(opts =>
                {
                    opts.ServiceId = ""; // Missing ServiceId
                    opts.ConnectionString = "amqp://guest:guest@localhost:5672/";
                }));
        }

        [Fact]
        public void RabbitMqAndServiceBusAreInterchangeable()
        {
            // Arrange
            var rbMqOptions = new LmtOptions
            {
                ServiceId = "test",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            var sbOptions = new LmtOptions
            {
                ServiceId = "test",
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test=="
            };

            // Act
            var rbMqSender = LmtMessageSenderFactory.Create(rbMqOptions);
            var sbSender = LmtMessageSenderFactory.Create(sbOptions);

            // Assert - Both should implement the same interface
            Assert.IsAssignableFrom<ILmtMessageSender>(rbMqSender);
            Assert.IsAssignableFrom<ILmtMessageSender>(sbSender);
            
            // Different types
            Assert.IsType<LmtRabbitMqSender>(rbMqSender);
            Assert.IsType<LmtServiceBusSender>(sbSender);

            rbMqSender.Dispose();
            sbSender.Dispose();
        }
    }
}
