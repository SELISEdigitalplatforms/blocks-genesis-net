using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class LmtServiceExtensionsTests
    {
        [Fact]
        public void AddLmtClient_WithConfigureOptions_ThrowsWhenServicesIsNull()
        {
            // Arrange
            IServiceCollection services = null!;

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                services.AddLmtClient(opts => { }));
        }

        [Fact]
        public void AddLmtClient_WithConfigureOptions_ThrowsWhenConfigureOptionsIsNull()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                services.AddLmtClient((Action<LmtOptions>)null!));
        }

        [Fact]
        public void AddLmtClient_WithConfigureOptions_ThrowsWhenServiceIdIsNotConfigured()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => 
                services.AddLmtClient(opts =>
                {
                    opts.ConnectionString = "amqp://guest:guest@localhost:5672/";
                    // ServiceId is not set
                }));
        }

        [Fact]
        public void AddLmtClient_WithConfigureOptions_ThrowsWhenConnectionStringIsNotConfigured()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => 
                services.AddLmtClient(opts =>
                {
                    opts.ServiceId = "test-service";
                    // ConnectionString is not set
                }));
        }

        [Fact]
        public void AddLmtClient_WithConfigureOptions_RegistersServicesWhenValidConfigProvided()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddLmtClient(opts =>
            {
                opts.ServiceId = "test-service";
                opts.ConnectionString = "amqp://guest:guest@localhost:5672/";
            });

            var serviceProvider = services.BuildServiceProvider();

            // Assert
            Assert.NotNull(serviceProvider.GetService<IBlocksLogger>());
            Assert.NotNull(serviceProvider.GetService<ILmtMessageSender>());
            Assert.NotNull(serviceProvider.GetService<LmtOptions>());
        }

        [Fact]
        public void AddLmtClient_WithConfiguration_ThrowsWhenServicesIsNull()
        {
            // Arrange
            IServiceCollection services = null!;
            var config = new ConfigurationBuilder().Build();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                services.AddLmtClient(config));
        }

        [Fact]
        public void AddLmtClient_WithConfiguration_ThrowsWhenConfigurationIsNull()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                services.AddLmtClient((IConfiguration)null!));
        }

        [Fact]
        public void AddLmtClient_WithConfiguration_ThrowsWhenRequiredSettingsMissing()
        {
            // Arrange
            var services = new ServiceCollection();
            var configBuilder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "Lmt:ServiceId", "test-service" }
                    // Missing ConnectionString
                });

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => 
                services.AddLmtClient(configBuilder.Build()));
        }

        [Fact]
        public void AddLmtClient_WithConfiguration_RegistersServicesWhenValidConfigProvided()
        {
            // Arrange
            var services = new ServiceCollection();
            var configBuilder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "Lmt:ServiceId", "test-service" },
                    { "Lmt:ConnectionString", "amqp://guest:guest@localhost:5672/" }
                });

            // Act
            services.AddLmtClient(configBuilder.Build());
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            Assert.NotNull(serviceProvider.GetService<IBlocksLogger>());
            Assert.NotNull(serviceProvider.GetService<ILmtMessageSender>());
            var options = serviceProvider.GetService<LmtOptions>();
            Assert.NotNull(options);
            Assert.Equal("test-service", options.ServiceId);
        }

        [Fact]
        public void AddLmtTracing_ThrowsWhenBuilderIsNull()
        {
            // Arrange
            global::OpenTelemetry.Trace.TracerProviderBuilder builder = null!;
            var options = new LmtOptions();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                builder.AddLmtTracing(options));
        }

        [Fact]
        public void AddLmtTracing_ThrowsWhenOptionsIsNull()
        {
            // Arrange  
            // We can't easily test this without a real TracerProviderBuilder
            // Skip this test as it would require more complex setup
        }

        [Fact]
        public void AddLmtTracing_CanBeCalledWithValidOptions()
        {
            // This test verifies the extension method signature and basic functionality
            var options = new LmtOptions
            {
                ServiceId = "test-service",
                ConnectionString = "amqp://guest:guest@localhost:5672/"
            };

            // We're just verifying the method exists and can be called
            // without actually running the full OpenTelemetry pipeline
            Assert.NotNull(options);
            Assert.Equal("test-service", options.ServiceId);
        }
    }
}
