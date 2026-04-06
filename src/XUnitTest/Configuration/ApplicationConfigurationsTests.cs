using Blocks.Genesis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using StackExchange.Redis;
using System.Reflection;

namespace XUnitTest.Configuration;

[Collection("DirectorySensitiveTests")]
public class ApplicationConfigurationsTests
{
    [Fact]
    public async Task ConfigureLogAndSecretsAsync_ShouldSetServiceName_ForOnPremVault()
    {
        var previousServiceName = Environment.GetEnvironmentVariable("BlocksSecret__ServiceName");
        var previousLog = Environment.GetEnvironmentVariable("BlocksSecret__LogConnectionString");
        var previousMetric = Environment.GetEnvironmentVariable("BlocksSecret__MetricConnectionString");
        var previousTrace = Environment.GetEnvironmentVariable("BlocksSecret__TraceConnectionString");
        var previousEnableHsts = Environment.GetEnvironmentVariable("BlocksSecret__EnableHsts");

        try
        {
            Environment.SetEnvironmentVariable("BlocksSecret__ServiceName", "env-will-be-overwritten");
            Environment.SetEnvironmentVariable("BlocksSecret__LogConnectionString", string.Empty);
            Environment.SetEnvironmentVariable("BlocksSecret__MetricConnectionString", string.Empty);
            Environment.SetEnvironmentVariable("BlocksSecret__TraceConnectionString", string.Empty);
            Environment.SetEnvironmentVariable("BlocksSecret__EnableHsts", "true");

            var secret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync("svc-config", VaultType.OnPrem);

            Assert.NotNull(secret);
            Assert.Equal("svc-config", secret.ServiceName);
            Assert.True(secret.EnableHsts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BlocksSecret__ServiceName", previousServiceName);
            Environment.SetEnvironmentVariable("BlocksSecret__LogConnectionString", previousLog);
            Environment.SetEnvironmentVariable("BlocksSecret__MetricConnectionString", previousMetric);
            Environment.SetEnvironmentVariable("BlocksSecret__TraceConnectionString", previousTrace);
            Environment.SetEnvironmentVariable("BlocksSecret__EnableHsts", previousEnableHsts);
        }
    }

    [Fact]
    public void ConfigureKestrel_ShouldConfigureBuilderPortsWithoutThrowing()
    {
        var previousHttp1 = Environment.GetEnvironmentVariable("HTTP1_PORT");
        var previousHttp2 = Environment.GetEnvironmentVariable("HTTP2_PORT");

        try
        {
            Environment.SetEnvironmentVariable("HTTP1_PORT", "5100");
            Environment.SetEnvironmentVariable("HTTP2_PORT", "5101");

            var builder = WebApplication.CreateBuilder();
            var ex = Record.Exception(() => ApplicationConfigurations.ConfigureKestrel(builder));

            Assert.Null(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP1_PORT", previousHttp1);
            Environment.SetEnvironmentVariable("HTTP2_PORT", previousHttp2);
        }
    }

    [Fact]
    public void ConfigureApi_AndConfigureMiddleware_ShouldExecutePipeline()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { EnableHsts = false });
        SetPrivateStaticField("_blocksSwaggerOptions", null);
        SetPrivateStaticField("_serviceName", "svc-pipeline");

        var builder = WebApplication.CreateBuilder();
        RegisterApiPrerequisites(builder.Services);
        ApplicationConfigurations.ConfigureApi(builder.Services);
        var app = builder.Build();

        var ex = Record.Exception(() => ApplicationConfigurations.ConfigureMiddleware(app));
        Assert.Null(ex);
    }

    [Fact]
    public void ConfigureMicroserviceMiddleware_ShouldInvokeAllOptionalHooks_WhenProvided()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { EnableHsts = true });
        SetPrivateStaticField("_blocksSwaggerOptions", new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v1",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        SetPrivateStaticField("_serviceName", "svc-hooks");

        var builder = WebApplication.CreateBuilder();
        RegisterApiPrerequisites(builder.Services);
        builder.Services.AddBlocksSwagger(new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v1",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        ApplicationConfigurations.ConfigureApi(builder.Services);
        var app = builder.Build();

        var beforeAuthCalled = false;
        var afterAuthorizationCalled = false;
        var beforeControllerMappingCalled = false;
        var afterControllerMappingCalled = false;

        ApplicationConfigurations.ConfigureMicroserviceMiddleware(
            app,
            beforeAuthentication: _ => beforeAuthCalled = true,
            afterAuthorization: _ => afterAuthorizationCalled = true,
            beforeControllerMapping: _ => beforeControllerMappingCalled = true,
            afterControllerMapping: _ => afterControllerMappingCalled = true);

        Assert.True(beforeAuthCalled);
        Assert.True(afterAuthorizationCalled);
        Assert.True(beforeControllerMappingCalled);
        Assert.True(afterControllerMappingCalled);
    }

    [Fact]
    public void GetAppSettingsFileName_ShouldReturnDefault_WhenEnvironmentIsMissing()
    {
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);

            var fileName = InvokeGetAppSettingsFileName();

            Assert.Equal("appsettings.json", fileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
        }
    }

    [Fact]
    public void GetAppSettingsFileName_ShouldReturnEnvironmentSpecificFile_WhenEnvironmentIsSet()
    {
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            var fileName = InvokeGetAppSettingsFileName();

            Assert.Equal("appsettings.Development.json", fileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
        }
    }

    [Fact]
    public void ConfigureApiEnv_ShouldLoadEnvironmentSpecificSettings_AndInitializeLmtProvider()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousMaxRetries = Environment.GetEnvironmentVariable("MaxRetries");
        var previousMaxFailedBatches = Environment.GetEnvironmentVariable("MaxFailedBatches");

        var tempDirectory = CreateTempDirectory();

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("MaxRetries", "99");
            Environment.SetEnvironmentVariable("MaxFailedBatches", "199");

            File.WriteAllText(Path.Combine(tempDirectory, "appsettings.Development.json"),
                """
                {
                  "Lmt": {
                    "MaxRetries": "7",
                    "MaxFailedBatches": "17"
                  },
                  "SwaggerOptions": {
                    "Title": "Test API",
                    "Version": "v-test"
                  }
                }
                """);

            Directory.SetCurrentDirectory(tempDirectory);

            InitializeBootstrapState("test-api-service");

            var builder = new HostApplicationBuilder();

            ApplicationConfigurations.ConfigureApiEnv(builder, Array.Empty<string>());

            Assert.Equal(7, InvokeLmtConfigurationProviderIntMethod("GetLmtMaxRetries"));
            Assert.Equal(17, InvokeLmtConfigurationProviderIntMethod("GetLmtMaxFailedBatches"));

            var swaggerOptions = GetBootstrapSwaggerOptions();
            Assert.NotNull(swaggerOptions);
            Assert.Equal("Test API", swaggerOptions.Title);
            Assert.Equal("v-test", swaggerOptions.Version);
        }
        finally
        {
            RestoreCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            Environment.SetEnvironmentVariable("MaxRetries", previousMaxRetries);
            Environment.SetEnvironmentVariable("MaxFailedBatches", previousMaxFailedBatches);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void ConfigureWorkerEnv_ShouldLoadEnvironmentSpecificSettings_AndInitializeLmtProvider()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousMaxRetries = Environment.GetEnvironmentVariable("MaxRetries");
        var previousMaxFailedBatches = Environment.GetEnvironmentVariable("MaxFailedBatches");

        var tempDirectory = CreateTempDirectory();

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Staging");
            Environment.SetEnvironmentVariable("MaxRetries", "12");
            Environment.SetEnvironmentVariable("MaxFailedBatches", "22");

            File.WriteAllText(Path.Combine(tempDirectory, "appsettings.Staging.json"),
                """
                {
                  "Lmt": {
                    "MaxRetries": "8",
                    "MaxFailedBatches": "18"
                  }
                }
                """);

            Directory.SetCurrentDirectory(tempDirectory);

            var configurationBuilder = new ConfigurationBuilder()
                .SetBasePath(tempDirectory);

            ApplicationConfigurations.ConfigureWorkerEnv(configurationBuilder, Array.Empty<string>());

            Assert.Equal(8, InvokeLmtConfigurationProviderIntMethod("GetLmtMaxRetries"));
            Assert.Equal(18, InvokeLmtConfigurationProviderIntMethod("GetLmtMaxFailedBatches"));
        }
        finally
        {
            RestoreCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            Environment.SetEnvironmentVariable("MaxRetries", previousMaxRetries);
            Environment.SetEnvironmentVariable("MaxFailedBatches", previousMaxFailedBatches);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void ConfigureApiEnv_ShouldLoadLmtConnectionStringFromConfiguration()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var tempDirectory = CreateTempDirectory();

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            File.WriteAllText(Path.Combine(tempDirectory, "appsettings.Development.json"),
                """
                {
                  "Lmt": {
                    "ConnectionString": "amqps://config-priority-host"
                  },
                  "SwaggerOptions": {
                    "Title": "Test API",
                    "Version": "v-test"
                  }
                }
                """);

            Directory.SetCurrentDirectory(tempDirectory);

            InitializeBootstrapState("test-api-service");

            var builder = new HostApplicationBuilder();
            ApplicationConfigurations.ConfigureApiEnv(builder, Array.Empty<string>());

            Assert.Equal("amqps://config-priority-host", InvokeLmtConfigurationProviderStringMethod("GetLmtConnectionString"));
        }
        finally
        {
            RestoreCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void LmtConfigurationProvider_ShouldUseEnvFallback_WhenLmtConnectionStringIsNotConfigured()
    {
        var previousLmt = Environment.GetEnvironmentVariable("Lmt__ConnectionString");

        try
        {
            InitializeLmtConfigurationProvider(new ConfigurationBuilder().Build());
            Environment.SetEnvironmentVariable("Lmt__ConnectionString", "Endpoint=sb://env-fallback");

            Assert.Equal("Endpoint=sb://env-fallback", InvokeLmtConfigurationProviderStringMethod("GetLmtConnectionString"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Lmt__ConnectionString", previousLmt);
        }
    }

    [Fact]
    public void LoadDotEnvFile_ShouldNotThrow_WhenEnvFileDoesNotExist()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = CreateTempDirectory();

        try
        {
            Directory.SetCurrentDirectory(tempDirectory);

            var exception = Record.Exception(InvokeLoadDotEnvFile);

            Assert.Null(exception);
        }
        finally
        {
            RestoreCurrentDirectory(previousDirectory);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void LoadDotEnvFile_ShouldLoadVariables_WhenEnvFileExists()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var previousValue = Environment.GetEnvironmentVariable("APP_CONFIG_TEST_KEY");
        var tempDirectory = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory, ".env"), "APP_CONFIG_TEST_KEY=from-dotenv");
            Environment.SetEnvironmentVariable("APP_CONFIG_TEST_KEY", null);
            Directory.SetCurrentDirectory(tempDirectory);

            InvokeLoadDotEnvFile();

            Assert.Equal("from-dotenv", Environment.GetEnvironmentVariable("APP_CONFIG_TEST_KEY"));
        }
        finally
        {
            RestoreCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("APP_CONFIG_TEST_KEY", previousValue);
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static string InvokeGetAppSettingsFileName()
    {
        var method = typeof(ApplicationConfigurations).GetMethod("GetAppSettingsFileName", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, null)!;
    }

    private static void InvokeLoadDotEnvFile()
    {
        var method = typeof(ApplicationConfigurations).GetMethod("LoadDotEnvFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, null);
    }

    private static void InitializeBootstrapState(string serviceName)
    {
        ApplicationConfigurations.ConfigureLogAndSecretsAsync(serviceName, SecretMode.OnPrem).GetAwaiter().GetResult();
    }

    private static BlocksSwaggerOptions? GetBootstrapSwaggerOptions()
    {
        var method = typeof(ApplicationConfigurations).GetMethod("GetBootstrapState", BindingFlags.NonPublic | BindingFlags.Static)!;
        var state = method.Invoke(null, null);
        var property = state?.GetType().GetProperty("SwaggerOptions", BindingFlags.Public | BindingFlags.Instance);
        return (BlocksSwaggerOptions?)property?.GetValue(state);
    }

    private static int InvokeLmtConfigurationProviderIntMethod(string methodName)
    {
        var providerType = typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = providerType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
        return (int)method.Invoke(null, null)!;
    }

    private static string? InvokeLmtConfigurationProviderStringMethod(string methodName)
    {
        var providerType = typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = providerType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
        return (string?)method.Invoke(null, null);
    }

    private static void InitializeLmtConfigurationProvider(IConfiguration configuration)
    {
        var providerType = typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = providerType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!;
        method.Invoke(null, [configuration]);
    }

    private static string CreateTempDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"genesis-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void RestoreCurrentDirectory(string previousDirectory)
    {
        if (!string.IsNullOrWhiteSpace(previousDirectory) && Directory.Exists(previousDirectory))
        {
            Directory.SetCurrentDirectory(previousDirectory);
            return;
        }

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    }
}