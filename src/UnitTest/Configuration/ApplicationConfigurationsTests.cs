using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog;
using System.Diagnostics;

namespace XUnitTest.Configuration
{
    public class ApplicationConfigurationsTests
    {
        private readonly Mock<IServiceCollection> _serviceCollectionMock;
        private readonly Mock<IConfiguration> _configurationMock;
        private readonly Mock<IBlocksSecret> _blocksSecretMock;
        private readonly WebApplicationBuilder _builder;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<ActivitySource> _activitySourceMock;

        public ApplicationConfigurationsTests()
        {
            _serviceCollectionMock = new Mock<IServiceCollection>();
            _configurationMock = new Mock<IConfiguration>();
            _blocksSecretMock = new Mock<IBlocksSecret>();
            _builder = WebApplication.CreateBuilder();
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _activitySourceMock = new Mock<ActivitySource>();
        }

        [Fact]
        public async Task ConfigureLogAndSecretsAsync_ShouldInitializeSecretsAndLogger()
        {
            // Arrange
            var serviceName = "TestService";
            var vaultConfig = new Dictionary<string, string>
        {
            { "Key1", "Value1" }
        };

            _blocksSecretMock.SetupProperty(s => s.ServiceName, serviceName);

            // Act
            var result = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(serviceName);

            // Assert
            Assert.Equal(serviceName, result.ServiceName);
            Assert.NotNull(Log.Logger); // Verify logger is initialized
        }

        [Fact]
        public void ConfigureAppConfigs_ShouldAddConfigurationSources()
        {
            // Arrange
            var args = new string[] { "--TestOption=Value" };

            // Act
            ApplicationConfigurations.ConfigureAppConfigs(_builder, args);

            // Assert
            Assert.NotNull(_builder.Configuration["TestOption"]);
            Assert.Equal("Value", _builder.Configuration["TestOption"]);
        }

        [Fact]
        public void ConfigureServices_ShouldRegisterDependencies()
        {
            // Arrange
            var messageConfiguration = new MessageConfiguration();

            // Act
            ApplicationConfigurations.ConfigureServices(_builder.Services, messageConfiguration);

            // Assert
            Assert.Contains(_builder.Services, s => s.ServiceType == typeof(IBlocksSecret));
            Assert.Contains(_builder.Services, s => s.ServiceType == typeof(ICacheClient));
            Assert.Contains(_builder.Services, s => s.ServiceType == typeof(IHttpService));
            Assert.Contains(_builder.Services, s => s.ServiceType == typeof(ActivitySource));
        }

        [Fact]
        public void ConfigureApi_ShouldRegisterJwtAndControllers()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            ApplicationConfigurations.ConfigureApi(services);

            // Assert
            Assert.Contains(services, s => s.ServiceType == typeof(IHttpClientFactory));
            Assert.Contains(services, s => s.ServiceType == typeof(IAuthenticationService));
        }

        [Fact]
        public async Task ConfigureWorker_ShouldAddHostedServices()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            ApplicationConfigurations.ConfigureWorker(services);

            // Assert
            Assert.Contains(services, s => s.ServiceType == typeof(AzureMessageWorker));
            Assert.Contains(services, s => s.ServiceType == typeof(HealthServiceWorker));
        }
    }
}




