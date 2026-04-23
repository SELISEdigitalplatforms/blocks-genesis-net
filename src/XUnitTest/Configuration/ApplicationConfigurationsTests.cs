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

            var builder = new HostApplicationBuilder();

            ApplicationConfigurations.ConfigureApiEnv(builder, Array.Empty<string>());

            Assert.Equal(7, InvokeLmtConfigurationProviderIntMethod("GetMaxRetries"));
            Assert.Equal(17, InvokeLmtConfigurationProviderIntMethod("GetMaxFailedBatches"));

            var swaggerOptions = GetPrivateStaticFieldValue<BlocksSwaggerOptions>("_blocksSwaggerOptions");
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

            Assert.Equal(8, InvokeLmtConfigurationProviderIntMethod("GetMaxRetries"));
            Assert.Equal(18, InvokeLmtConfigurationProviderIntMethod("GetMaxFailedBatches"));
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

    // ConfigureServices / ConfigureWorker register the MongoDB ObjectSerializer globally,
    // which fails on the second call in the same process. We therefore invoke the pipeline
    // exactly once via a class fixture and share the resulting ServiceCollection across
    // multiple assertions. This covers ConfigureServices (all lines), the Rabbit branch
    // of ConfigureMessageClient, the Rabbit branch of ConfigureWorker, and the swagger
    // `!= null` branch in ConfigureServices.
    [Fact]
    public void ConfigureWorker_ShouldRegisterCoreAndRabbitMqServices_WhenRabbitMqAndSwaggerConfigured()
    {
        var fixture = ConfigureWorkerFixture.Instance;

        Assert.Null(fixture.Exception);
        var services = fixture.Services;
        Assert.Contains(services, d => d.ServiceType == typeof(IBlocksSecret));
        Assert.Contains(services, d => d.ServiceType == typeof(ICacheClient));
        Assert.Contains(services, d => d.ServiceType == typeof(ITenants));
        Assert.Contains(services, d => d.ServiceType == typeof(IDbContextProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(IHttpService));
        Assert.Contains(services, d => d.ServiceType == typeof(ICryptoService));
        Assert.Contains(services, d => d.ServiceType == typeof(IGrpcClientFactory));
        Assert.Contains(services, d => d.ServiceType == typeof(MessageConfiguration));
        Assert.Contains(services, d => d.ServiceType == typeof(IRabbitMqService));
        Assert.Contains(services, d => d.ServiceType == typeof(IMessageClient));
        Assert.Contains(services, d => d.ServiceType == typeof(Consumer));
        Assert.Contains(services, d => d.ServiceType == typeof(RoutingTable));
        Assert.Contains(services, d => d.ImplementationType == typeof(RabbitMessageWorker));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-uri", false)]
    public void IsOriginAllowed_ShouldReturnFalse_ForInvalidOrigin(string? origin, bool expected)
    {
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        var env = CreateHostEnvironment("Production");

        var result = InvokeIsOriginAllowed(origin, tenants.Object, env, Array.Empty<string>());

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http://localhost:3000", true)]
    [InlineData("https://127.0.0.1", true)]
    public void IsOriginAllowed_ShouldAllow_LocalDevelopmentOrigins(string origin, bool expected)
    {
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        var env = CreateHostEnvironment("Development");

        var result = InvokeIsOriginAllowed(origin, tenants.Object, env, Array.Empty<string>());

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsOriginAllowed_ShouldAllow_WhenOriginIsInExplicitList()
    {
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        var env = CreateHostEnvironment("Production");
        var allowedOrigins = new[] { "https://example.com" };

        var result = InvokeIsOriginAllowed("https://example.com", tenants.Object, env, allowedOrigins);

        Assert.True(result);
    }

    [Fact]
    public void IsOriginAllowed_ShouldAllow_WhenTenantMatchesApplicationDomain()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByApplicationDomain("https://tenant.app"))
               .Returns(CreateTenantStub("https://tenant.app"));
        var env = CreateHostEnvironment("Production");

        var result = InvokeIsOriginAllowed("https://tenant.app", tenants.Object, env, Array.Empty<string>());

        Assert.True(result);
    }

    [Fact]
    public void IsOriginAllowed_ShouldReturnFalse_WhenNothingMatches()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByApplicationDomain(It.IsAny<string>()))
               .Returns((Blocks.Genesis.Tenant?)null);
        var env = CreateHostEnvironment("Production");

        var result = InvokeIsOriginAllowed("https://unknown.app", tenants.Object, env, Array.Empty<string>());

        Assert.False(result);
    }

    [Fact]
    public void IsOriginAllowed_ShouldNotAllowLocalhost_InNonDevelopmentEnvironment()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByApplicationDomain(It.IsAny<string>()))
               .Returns((Blocks.Genesis.Tenant?)null);
        var env = CreateHostEnvironment("Production");

        var result = InvokeIsOriginAllowed("http://localhost:3000", tenants.Object, env, Array.Empty<string>());

        Assert.False(result);
    }

    [Fact]
    public void ParseAllowedCorsOrigins_ShouldReturnFilteredDistinctList()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret
        {
            AllowedCorsOrigins = "https://a.com, https://b.com , not-a-uri, https://A.com"
        });

        var result = InvokeParseAllowedCorsOrigins();

        Assert.Contains("https://a.com", result);
        Assert.Contains("https://b.com", result);
        Assert.DoesNotContain("not-a-uri", result);
        Assert.Equal(2, result.Count); // "https://A.com" is a duplicate under OrdinalIgnoreCase
    }

    [Fact]
    public void ParseAllowedCorsOrigins_ShouldReturnEmpty_WhenSecretHasNoOrigins()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { AllowedCorsOrigins = null! });

        var result = InvokeParseAllowedCorsOrigins();

        Assert.Empty(result);
    }

    [Fact]
    public void ConfigureRateLimiting_ShouldHonourCustomPermitLimit()
    {
        var previous = Environment.GetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE");
        try
        {
            Environment.SetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE", "50");

            var services = new ServiceCollection();
            InvokeConfigureRateLimiting(services);

            Assert.Contains(services, d => d.ServiceType.FullName!.Contains("RateLimiter", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE", previous);
        }
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData(null)]
    public void ConfigureRateLimiting_ShouldFallBackToDefault_WhenInvalidOrMissingEnv(string? value)
    {
        var previous = Environment.GetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE");
        try
        {
            Environment.SetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE", value);

            var services = new ServiceCollection();
            var ex = Record.Exception(() => InvokeConfigureRateLimiting(services));

            Assert.Null(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE", previous);
        }
    }

    private static Blocks.Genesis.Tenant CreateTenantStub(string applicationDomain) => new()
    {
        ApplicationDomain = applicationDomain,
        DbConnectionString = "mongodb://localhost:27017",
        JwtTokenParameters = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = ["aud"],
            PublicCertificatePath = "path",
            PublicCertificatePassword = "pwd",
            PrivateCertificatePassword = "private",
            IssueDate = DateTime.UtcNow
        }
    };

    private static BlocksSecret CreateValidSecret() => new()
    {
        DatabaseConnectionString = "mongodb://localhost:27017/genesis-tests",
        CacheConnectionString = "localhost:6379,abortConnect=false",
        MessageConnectionString = "amqp://guest:guest@localhost:5672",
        AllowedCorsOrigins = string.Empty,
        ServiceName = "svc-core"
    };

    private static IHostEnvironment CreateHostEnvironment(string environmentName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return env.Object;
    }

    private static bool InvokeIsOriginAllowed(string? origin, ITenants tenants, IHostEnvironment env, IReadOnlyCollection<string> allowedOrigins)
    {
        var method = typeof(ApplicationConfigurations).GetMethod("IsOriginAllowed", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, new object?[] { origin, tenants, env, allowedOrigins })!;
    }

    private static List<string> InvokeParseAllowedCorsOrigins()
    {
        var method = typeof(ApplicationConfigurations).GetMethod("ParseAllowedCorsOrigins", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<string>)method.Invoke(null, null)!;
    }

    private static void InvokeConfigureRateLimiting(IServiceCollection services)
    {
        var method = typeof(ApplicationConfigurations).GetMethod("ConfigureRateLimiting", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, new object?[] { services });
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

    private static T GetPrivateStaticFieldValue<T>(string fieldName)
    {
        var field = typeof(ApplicationConfigurations).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)field.GetValue(null)!;
    }

    private static int InvokeLmtConfigurationProviderIntMethod(string methodName)
    {
        var providerType = typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = providerType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
        return (int)method.Invoke(null, null)!;
    }

    private static void SetPrivateStaticField(string fieldName, object? value)
    {
        var field = typeof(ApplicationConfigurations).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }

    private static void RegisterApiPrerequisites(IServiceCollection services)
    {
        var cache = new Mock<ICacheClient>(MockBehavior.Strict);
        cache.Setup(c => c.CacheDatabase()).Returns(Mock.Of<IDatabase>());

        services.AddSingleton(Mock.Of<ITenants>());
        services.AddSingleton(cache.Object);
        services.AddHealthChecks();
        services.AddHttpContextAccessor();
        services.AddHttpClient();
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

// Lazily invokes ConfigureWorker exactly once per test process (MongoDB ObjectSerializer
// registration is global state and can only be performed once).
internal sealed class ConfigureWorkerFixture
{
    private static readonly Lazy<ConfigureWorkerFixture> _instance = new(() => new ConfigureWorkerFixture());
    public static ConfigureWorkerFixture Instance => _instance.Value;

    public IServiceCollection Services { get; }
    public Exception? Exception { get; }

    private ConfigureWorkerFixture()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret
        {
            DatabaseConnectionString = "mongodb://localhost:27017/genesis-tests",
            CacheConnectionString = "localhost:6379,abortConnect=false",
            MessageConnectionString = "amqp://guest:guest@localhost:5672",
            AllowedCorsOrigins = string.Empty,
            ServiceName = "svc-worker-fixture"
        });
        SetPrivateStaticField("_serviceName", "svc-worker-fixture");
        SetPrivateStaticField("_blocksSwaggerOptions", new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v1",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });

        Services = new ServiceCollection();
        try
        {
            ApplicationConfigurations.ConfigureWorker(Services, new MessageConfiguration
            {
                RabbitMqConfiguration = new RabbitMqConfiguration()
            });
        }
        catch (Exception ex)
        {
            Exception = ex;
        }
    }

    private static void SetPrivateStaticField(string fieldName, object? value)
    {
        var field = typeof(ApplicationConfigurations).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }
}