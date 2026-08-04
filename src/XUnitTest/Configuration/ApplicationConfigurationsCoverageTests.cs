using Blocks.Genesis;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization;
using Moq;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;

namespace XUnitTest.Configuration;

[Collection("DirectorySensitiveTests")]
public class ApplicationConfigurationsCoverageTests
{
    [Fact]
    public void ResolveVaultType_ShouldReturnDefault_WhenEnvironmentVariableIsMissing()
    {
        var previousVaultType = Environment.GetEnvironmentVariable("BLOCKS_VAULT_TYPE");
        var previousDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = CreateTempDirectory();

        try
        {
            Environment.SetEnvironmentVariable("BLOCKS_VAULT_TYPE", null);
            Directory.SetCurrentDirectory(tempDirectory);

            Assert.Equal(VaultType.Azure, ApplicationConfigurations.ResolveVaultType());
            Assert.Equal(VaultType.OnPrem, ApplicationConfigurations.ResolveVaultType(VaultType.OnPrem));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLOCKS_VAULT_TYPE", previousVaultType);
            RestoreCurrentDirectory(previousDirectory);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void ResolveVaultType_ShouldReturnParsedValue_WhenEnvironmentVariableIsValid()
    {
        var previousVaultType = Environment.GetEnvironmentVariable("BLOCKS_VAULT_TYPE");
        var previousDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = CreateTempDirectory();

        try
        {
            Environment.SetEnvironmentVariable("BLOCKS_VAULT_TYPE", "onprem");
            Directory.SetCurrentDirectory(tempDirectory);

            Assert.Equal(VaultType.OnPrem, ApplicationConfigurations.ResolveVaultType());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLOCKS_VAULT_TYPE", previousVaultType);
            RestoreCurrentDirectory(previousDirectory);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Theory]
    [InlineData("not-a-vault")]
    [InlineData("   ")]
    public void ResolveVaultType_ShouldReturnDefault_WhenEnvironmentVariableIsInvalid(string configuredValue)
    {
        var previousVaultType = Environment.GetEnvironmentVariable("BLOCKS_VAULT_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("BLOCKS_VAULT_TYPE", configuredValue);

            Assert.Equal(VaultType.Azure, ApplicationConfigurations.ResolveVaultType());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLOCKS_VAULT_TYPE", previousVaultType);
        }
    }

    [Fact]
    public void ConfigureKestrel_ShouldUseDefaultPorts_WhenEnvironmentVariablesAreMissing()
    {
        var previousHttp1 = Environment.GetEnvironmentVariable("HTTP1_PORT");
        var previousHttp2 = Environment.GetEnvironmentVariable("HTTP2_PORT");

        try
        {
            Environment.SetEnvironmentVariable("HTTP1_PORT", null);
            Environment.SetEnvironmentVariable("HTTP2_PORT", null);

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
    public async Task ConfigureLogAndSecretsAsync_ShouldCreateLogCollection_WhenLogConnectionStringIsConfigured()
    {
        var previousServiceName = Environment.GetEnvironmentVariable("BlocksSecret__ServiceName");
        var previousLog = Environment.GetEnvironmentVariable("BlocksSecret__LogConnectionString");
        var previousMetric = Environment.GetEnvironmentVariable("BlocksSecret__MetricConnectionString");
        var previousTrace = Environment.GetEnvironmentVariable("BlocksSecret__TraceConnectionString");

        const string unreachableMongo =
            "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100&socketTimeoutMS=100";

        try
        {
            Environment.SetEnvironmentVariable("BlocksSecret__ServiceName", "env-will-be-overwritten");
            Environment.SetEnvironmentVariable("BlocksSecret__LogConnectionString", unreachableMongo);
            Environment.SetEnvironmentVariable("BlocksSecret__MetricConnectionString", string.Empty);
            Environment.SetEnvironmentVariable("BlocksSecret__TraceConnectionString", string.Empty);

            var secret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync("svc-log-config", VaultType.OnPrem);

            Assert.NotNull(secret);
            Assert.Equal("svc-log-config", secret.ServiceName);
            Assert.Equal(unreachableMongo, secret.LogConnectionString);
        }
        finally
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
            Environment.SetEnvironmentVariable("BlocksSecret__ServiceName", previousServiceName);
            Environment.SetEnvironmentVariable("BlocksSecret__LogConnectionString", previousLog);
            Environment.SetEnvironmentVariable("BlocksSecret__MetricConnectionString", previousMetric);
            Environment.SetEnvironmentVariable("BlocksSecret__TraceConnectionString", previousTrace);
        }
    }

    [Fact]
    public void ConfigureApi_ShouldThrow_WhenServicesIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ApplicationConfigurations.ConfigureApi(null!, "svc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ConfigureApi_ShouldThrow_WhenServiceNameIsMissing(string? serviceName)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => ApplicationConfigurations.ConfigureApi(services, serviceName!));

        Assert.Equal("serviceName", ex.ParamName);
    }

    [Fact]
    public void ConfigureApi_ShouldUseTrimmedResourceName_WhenServiceAccessResourceNameIsProvided()
    {
        var services = new ServiceCollection();

        ApplicationConfigurations.ConfigureApi(services, " svc-resource ", serviceAccessResourceName: " custom-resource ");

        Assert.Equal("custom-resource", GetServiceAccessResourceName());
    }

    [Fact]
    public void ConfigureApi_ShouldFallBackToServiceName_WhenServiceAccessResourceNameIsMissing()
    {
        var services = new ServiceCollection();

        ApplicationConfigurations.ConfigureApi(services, " svc-fallback ", serviceAccessResourceName: "  ");

        Assert.Equal("svc-fallback", GetServiceAccessResourceName());
    }

    [Theory]
    [InlineData(null, "api")]
    [InlineData("   ", "api")]
    [InlineData("off", "")]
    [InlineData("NONE", "")]
    [InlineData("False", "")]
    [InlineData("/v2/", "v2")]
    [InlineData("///", "api")]
    [InlineData("custom", "custom")]
    public void NormalizeApiRoutePrefixValue_ShouldNormalizePrefix(string? apiRoutePrefix, string expected)
    {
        Assert.Equal(expected, ApplicationConfigurations.NormalizeApiRoutePrefixValue(apiRoutePrefix));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("manage", "/manage")]
    [InlineData("/manage", "/manage")]
    [InlineData(" manage/ ", "/manage")]
    public void NormalizePathBase_ShouldNormalizeValue(string? rawPathBase, string expected)
    {
        var method = typeof(ApplicationConfigurations).GetMethod("NormalizePathBase", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expected, (string)method.Invoke(null, [rawPathBase])!);
    }

    [Fact]
    public void ParseAllowedCorsOrigins_ShouldReturnEmptyList_WhenBlocksSecretIsNull()
    {
        var previousSecret = GetPrivateStaticFieldValue<object?>("_blocksSecret");

        try
        {
            SetPrivateStaticField("_blocksSecret", null);

            var origins = InvokeParseAllowedCorsOrigins();

            Assert.Empty(origins);
        }
        finally
        {
            SetPrivateStaticField("_blocksSecret", previousSecret);
        }
    }

    [Fact]
    public void ParseAllowedCorsOrigins_ShouldFilterInvalidAndDuplicateOrigins()
    {
        var previousSecret = GetPrivateStaticFieldValue<object?>("_blocksSecret");

        try
        {
            SetPrivateStaticField("_blocksSecret", new BlocksSecret
            {
                AllowedCorsOrigins = "https://a.example.com, not-a-url, HTTPS://A.EXAMPLE.COM, ,https://b.example.com"
            });

            var origins = InvokeParseAllowedCorsOrigins();

            Assert.Equal(2, origins.Count);
            Assert.Contains("https://a.example.com", origins);
            Assert.Contains("https://b.example.com", origins);
        }
        finally
        {
            SetPrivateStaticField("_blocksSecret", previousSecret);
        }
    }

    [Theory]
    [InlineData(null, "Production", false)]
    [InlineData("not-a-url", "Production", false)]
    [InlineData("http://localhost:3000", "Development", true)]
    [InlineData("http://127.0.0.1:8080", "Development", true)]
    [InlineData("https://unknown.example.com", "Development", false)]
    [InlineData("http://localhost:3000", "Production", false)]
    public void IsOriginAllowed_ShouldEvaluateEnvironmentAndTenantRules(string? origin, string environmentName, bool expected)
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByApplicationDomain(It.IsAny<string>())).Returns((Blocks.Genesis.Tenant?)null);

        var result = InvokeIsOriginAllowed(origin, tenants.Object, environmentName, []);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsOriginAllowed_ShouldReturnTrue_WhenOriginIsInAllowedList()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByApplicationDomain(It.IsAny<string>())).Returns((Blocks.Genesis.Tenant?)null);

        var result = InvokeIsOriginAllowed(
            "https://allowed.example.com",
            tenants.Object,
            "Production",
            ["HTTPS://ALLOWED.EXAMPLE.COM"]);

        Assert.True(result);
    }

    [Fact]
    public void IsOriginAllowed_ShouldReturnTrue_WhenTenantMatchesApplicationDomain()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByApplicationDomain("https://tenant.example.com")).Returns(CreateTenant());

        var result = InvokeIsOriginAllowed(
            "https://tenant.example.com/some/path",
            tenants.Object,
            "Production",
            []);

        Assert.True(result);
        tenants.Verify(t => t.GetTenantByApplicationDomain("https://tenant.example.com"), Times.Once);
    }

    [Fact]
    public void GetAppSettingsFileName_ShouldUseDotnetEnvironment_WhenAspNetCoreEnvironmentIsMissing()
    {
        var previousAspNetCore = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Staging");

            var method = typeof(ApplicationConfigurations).GetMethod("GetEnvironmentAppSettingsFileName", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal("appsettings.Staging.json", (string)method.Invoke(null, null)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetCore);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
        }
    }

    [Fact]
    public void LoadDotEnvFile_ShouldSwallowException_WhenEnvFileIsMalformed()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory, ".env"), "this is not a valid env line");
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
    public void LoadDotEnvFile_ShouldLoadVariables_WhenEnvFileIsNestedUnderServerDirectory()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var previousValue = Environment.GetEnvironmentVariable("APP_CONFIG_NESTED_TEST_KEY");
        var tempDirectory = CreateTempDirectory();

        try
        {
            var serverDirectory = Path.Combine(tempDirectory, "server");
            Directory.CreateDirectory(serverDirectory);
            File.WriteAllText(Path.Combine(serverDirectory, ".env"), "APP_CONFIG_NESTED_TEST_KEY=from-nested-dotenv");
            Environment.SetEnvironmentVariable("APP_CONFIG_NESTED_TEST_KEY", null);
            Directory.SetCurrentDirectory(tempDirectory);

            InvokeLoadDotEnvFile();

            Assert.Equal("from-nested-dotenv", Environment.GetEnvironmentVariable("APP_CONFIG_NESTED_TEST_KEY"));
        }
        finally
        {
            RestoreCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("APP_CONFIG_NESTED_TEST_KEY", previousValue);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Theory]
    [InlineData("", "localhost:6379", "DatabaseConnectionString")]
    [InlineData("mongodb://127.0.0.1:27017/db", "", "CacheConnectionString")]
    [InlineData("", "", "DatabaseConnectionString, CacheConnectionString")]
    public void ConfigureServices_ShouldThrow_WhenRequiredSecretsAreMissing(string databaseConnectionString, string cacheConnectionString, string expectedMissing)
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret
        {
            DatabaseConnectionString = databaseConnectionString,
            CacheConnectionString = cacheConnectionString
        });

        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration()));

        Assert.Contains(expectedMissing, ex.Message);
    }

    [Fact]
    public void ConfigureWorker_ShouldRegisterMessagingServices_ForAzureAndRabbitConfigurations()
    {
        var previousServiceBusConnection = Environment.GetEnvironmentVariable("ServiceBusConnectionString");

        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            SetPrivateStaticField("_serviceName", "svc-worker");
            SetPrivateStaticField("_blocksSwaggerOptions", null);
            SetPrivateStaticField("_blocksSecret", new BlocksSecret
            {
                DatabaseConnectionString = "mongodb://127.0.0.1:27017/genesis-worker-tests",
                CacheConnectionString = "localhost:6379,abortConnect=false",
                MessageConnectionString = string.Empty,
                TraceConnectionString = string.Empty
            });

            RemoveRegisteredObjectBsonSerializer();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

            var messageConfiguration = new MessageConfiguration
            {
                AzureServiceBusConfiguration = new AzureServiceBusConfiguration(),
                RabbitMqConfiguration = new RabbitMqConfiguration()
            };

            ApplicationConfigurations.ConfigureWorker(services, messageConfiguration);

            Assert.Equal(string.Empty, messageConfiguration.Connection);
            Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IMessageClient)));
            Assert.Contains(services, d => d.ServiceType == typeof(IRabbitMqService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AzureMessageWorker));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RabbitMessageWorker));
            Assert.Contains(services, d => d.ServiceType == typeof(Consumer));
            Assert.Contains(services, d => d.ServiceType == typeof(RoutingTable));

            var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<TracerProvider>());
            Assert.NotNull(provider.GetRequiredService<MeterProvider>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBusConnection);
        }
    }

    [Fact]
    public void ConfigureApi_ShouldConfigureRateLimiting_WhenPermitLimitEnvironmentVariableIsSet()
    {
        var previousPermitLimit = Environment.GetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE");

        try
        {
            Environment.SetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE", "5");

            var services = new ServiceCollection();
            var ex = Record.Exception(() => ApplicationConfigurations.ConfigureApi(services, "svc-rate-limit"));

            Assert.Null(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE", previousPermitLimit);
        }
    }

    [Fact]
    public void ConfigureWorker_ShouldRegisterSwaggerAndKeepPresetConnection_WhenMessagingConfigurationsAreMissing()
    {
        var secret = new BlocksSecret
        {
            DatabaseConnectionString = "mongodb://127.0.0.1:27017",
            CacheConnectionString = "localhost:6379,abortConnect=false",
            MessageConnectionString = "will-not-be-used"
        };

        SetPrivateStaticField("_serviceName", "svc-services");
        SetPrivateStaticField("_blocksSecret", secret);
        SetPrivateStaticField("_blocksSwaggerOptions", new BlocksSwaggerOptions
        {
            ServiceName = "svc-services",
            Version = "v1",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });

        RemoveRegisteredObjectBsonSerializer();

        var services = new ServiceCollection();
        var messageConfiguration = new MessageConfiguration { Connection = "preset-connection" };

        ApplicationConfigurations.ConfigureWorker(services, messageConfiguration);

        Assert.Equal("preset-connection", messageConfiguration.Connection);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMessageClient));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AzureMessageWorker));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RabbitMessageWorker));
        Assert.Contains(services, d => d.ServiceType == typeof(IBlocksSecret) && ReferenceEquals(d.ImplementationInstance, secret));
        Assert.Contains(services, d => d.ServiceType.Name == "ISwaggerProvider");
        Assert.Contains(services, d => d.ServiceType == typeof(RoutingTable));
    }

    [Fact]
    public void ConfigureMicroserviceMiddleware_ShouldThrow_WhenAppIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ApplicationConfigurations.ConfigureMicroserviceMiddleware(null!));
    }

    [Fact]
    public void ConfigureApiBranchMiddleware_ShouldThrow_WhenAppIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ApplicationConfigurations.ConfigureApiBranchMiddleware(null!));
    }

    [Fact]
    public void ConfigureMiddleware_ShouldApplyPathBaseAndTenantPrefixes_WhenConfigured()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret
        {
            EnableHsts = false,
            AllowedCorsOrigins = "https://allowed.example.com"
        });
        SetPrivateStaticField("_blocksSwaggerOptions", new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = string.Empty,
            PathBase = "manage/",
            EndpointUrl = "/swagger/v1/swagger.json",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        SetPrivateStaticField("_serviceName", "svc-pathbase");

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["EnableHsts"] = "true";
        RegisterApiPrerequisites(builder.Services);
        builder.Services.AddBlocksSwagger(new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v1",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        ApplicationConfigurations.ConfigureApi(builder.Services, "svc-pathbase");
        var app = builder.Build();

        var ex = Record.Exception(() => ApplicationConfigurations.ConfigureMiddleware(app, tenantValidationPrefixes: new[] { "api" }));

        Assert.Null(ex);
    }

    [Fact]
    public void ConfigureMicroserviceMiddleware_ShouldNotPrefixSwaggerEndpoint_WhenEndpointDoesNotStartWithSlash()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { EnableHsts = false });
        SetPrivateStaticField("_blocksSwaggerOptions", new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v2",
            PathBase = "/manage",
            EndpointUrl = "swagger/v1/swagger.json",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        SetPrivateStaticField("_serviceName", "svc-endpoint");

        var builder = WebApplication.CreateBuilder();
        RegisterApiPrerequisites(builder.Services);
        builder.Services.AddBlocksSwagger(new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v2",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        ApplicationConfigurations.ConfigureApi(builder.Services, "svc-endpoint");
        var app = builder.Build();

        var ex = Record.Exception(() => ApplicationConfigurations.ConfigureMicroserviceMiddleware(app));

        Assert.Null(ex);
    }

    [Fact]
    public void ConfigureKestrel_ShouldApplyEndpointCallbacks_WhenOptionsAreResolved()
    {
        var previousHttp1 = Environment.GetEnvironmentVariable("HTTP1_PORT");
        var previousHttp2 = Environment.GetEnvironmentVariable("HTTP2_PORT");

        try
        {
            Environment.SetEnvironmentVariable("HTTP1_PORT", "5111");
            Environment.SetEnvironmentVariable("HTTP2_PORT", "5112");

            var builder = WebApplication.CreateBuilder();
            ApplicationConfigurations.ConfigureKestrel(builder);
            using var app = builder.Build();

            // Resolving the options executes the ConfigureKestrel callback,
            // including both ListenAnyIP endpoint lambdas. Nothing binds until
            // the server starts, which this test never does.
            var options = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

            Assert.Equal(10 * 1024 * 1024, options.Limits.MaxRequestBodySize);

            var codeBacked = options.GetType()
                .GetProperty("CodeBackedListenOptions", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(codeBacked);
            var listenOptions = ((System.Collections.IEnumerable)codeBacked!.GetValue(options)!)
                .Cast<object>()
                .Select(o => (
                    Port: ((System.Net.IPEndPoint)o.GetType().GetProperty("IPEndPoint")!.GetValue(o)!).Port,
                    Protocols: (HttpProtocols)o.GetType().GetProperty("Protocols")!.GetValue(o)!))
                .ToList();

            Assert.Contains(listenOptions, l => l.Port == 5111 && l.Protocols == HttpProtocols.Http1);
            Assert.Contains(listenOptions, l => l.Port == 5112 && l.Protocols == HttpProtocols.Http2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP1_PORT", previousHttp1);
            Environment.SetEnvironmentVariable("HTTP2_PORT", previousHttp2);
        }
    }

    [Fact]
    public void ConfigureApi_ShouldAddGrpcServerInterceptor_WhenGrpcOptionsAreResolved()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { EnableHsts = false });
        SetPrivateStaticField("_blocksSwaggerOptions", null);
        SetPrivateStaticField("_serviceName", "svc-grpc-options");

        var builder = WebApplication.CreateBuilder();
        RegisterApiPrerequisites(builder.Services);
        ApplicationConfigurations.ConfigureApi(builder.Services, "svc-grpc-options");
        using var app = builder.Build();

        var grpcOptions = app.Services.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        Assert.Contains(grpcOptions.Interceptors, r => r.Type == typeof(GrpcServerInterceptor));
    }

    [Fact]
    public async Task ConfigureApi_RateLimiterPartitioner_ShouldPartitionByTenantHeaderOrClientIp()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { EnableHsts = false });
        SetPrivateStaticField("_blocksSwaggerOptions", null);
        SetPrivateStaticField("_serviceName", "svc-rate-limit");

        var builder = WebApplication.CreateBuilder();
        RegisterApiPrerequisites(builder.Services);
        ApplicationConfigurations.ConfigureApi(builder.Services, "svc-rate-limit");
        using var app = builder.Build();

        var limiterOptions = app.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.NotNull(limiterOptions.GlobalLimiter);

        // No tenant header and no remote address: partitions on "ip:unknown".
        var anonymousContext = new DefaultHttpContext();
        using var ipLease = await limiterOptions.GlobalLimiter!.AcquireAsync(anonymousContext);
        Assert.True(ipLease.IsAcquired);

        // Tenant header present: partitions on the tenant id.
        var tenantContext = new DefaultHttpContext();
        tenantContext.Request.Headers["tenant-id"] = "tenant-1";
        tenantContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        using var tenantLease = await limiterOptions.GlobalLimiter.AcquireAsync(tenantContext);
        Assert.True(tenantLease.IsAcquired);
    }

    [Fact]
    public async Task ConfigureMiddleware_ShouldServeHealthAndSwaggerEndpoints_WhenServerRuns()
    {
        SetPrivateStaticField("_blocksSecret", new BlocksSecret { EnableHsts = false });
        SetPrivateStaticField("_blocksSwaggerOptions", new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v1",
            PathBase = "/svc",
            EndpointUrl = "/swagger/v1/swagger.json",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        SetPrivateStaticField("_serviceName", "svc-live-endpoints");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        RegisterApiPrerequisites(builder.Services);
        builder.Services.AddSingleton<IBlocksSecret>(new BlocksSecret { EnableHsts = false });
        builder.Services.AddSingleton(Mock.Of<ICryptoService>());
        builder.Services.AddHealthChecks()
            .AddCheck("ready-probe", () => HealthCheckResult.Healthy(), tags: ["ready"]);
        builder.Services.AddBlocksSwagger(new BlocksSwaggerOptions
        {
            ServiceName = "svc",
            Version = "v1",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = false
        });
        ApplicationConfigurations.ConfigureApi(builder.Services, "svc-live-endpoints");
        var app = builder.Build();
        ApplicationConfigurations.ConfigureMiddleware(app);

        await app.StartAsync();
        try
        {
            var baseAddress = app.Urls.First();
            using var http = new HttpClient();

            var ping = await http.GetAsync($"{baseAddress}/ping");
            var live = await http.GetAsync($"{baseAddress}/health/live");
            var ready = await http.GetAsync($"{baseAddress}/health/ready");
            var swaggerDoc = await http.GetStringAsync($"{baseAddress}/swagger/v1/swagger.json");

            Assert.Equal(System.Net.HttpStatusCode.OK, ping.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);

            // The pre-serialize filter rewrites the server url to the path base.
            Assert.Contains("/svc", swaggerDoc);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConfigureServices_HealthChecks_ShouldReportMongoAndRedis_WhenExecuted()
    {
        if (!await IsMongoAvailable() || !await IsRedisAvailable())
        {
            return;
        }

        SetPrivateStaticField("_serviceName", "svc-health-exec");
        SetPrivateStaticField("_blocksSwaggerOptions", null);
        SetPrivateStaticField("_blocksSecret", new BlocksSecret
        {
            DatabaseConnectionString = "mongodb://127.0.0.1:27017/genesis-health-check-tests",
            CacheConnectionString = "localhost:6379,abortConnect=false",
            MessageConnectionString = string.Empty,
            TraceConnectionString = string.Empty
        });

        RemoveRegisteredObjectBsonSerializer();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
        {
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration(),
            RabbitMqConfiguration = new RabbitMqConfiguration()
        });

        await using var provider = services.BuildServiceProvider();
        var healthService = provider.GetRequiredService<HealthCheckService>();

        var report = await healthService.CheckHealthAsync();

        Assert.Contains("mongodb", report.Entries.Keys);
        Assert.Contains("redis", report.Entries.Keys);
        Assert.Equal(HealthStatus.Healthy, report.Entries["mongodb"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["redis"].Status);
    }

    private static async Task<bool> IsMongoAvailable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", 27017);
            var timeout = Task.Delay(TimeSpan.FromSeconds(2));
            var completed = await Task.WhenAny(connectTask, timeout);
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsRedisAvailable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", 6379);
            var timeout = Task.Delay(TimeSpan.FromSeconds(2));
            var completed = await Task.WhenAny(connectTask, timeout);
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static Blocks.Genesis.Tenant CreateTenant()
    {
        return new Blocks.Genesis.Tenant
        {
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                PrivateCertificatePassword = "test-password",
                IssueDate = DateTime.UtcNow
            }
        };
    }

    private static string? GetServiceAccessResourceName()
    {
        var property = typeof(ApplicationConfigurations).GetProperty("ServiceAccessResourceName", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string?)property.GetValue(null);
    }

    private static List<string> InvokeParseAllowedCorsOrigins()
    {
        var method = typeof(ApplicationConfigurations).GetMethod("ParseAllowedCorsOrigins", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<string>)method.Invoke(null, null)!;
    }

    private static bool InvokeIsOriginAllowed(string? origin, ITenants tenants, string environmentName, List<string> allowedOrigins)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var method = typeof(ApplicationConfigurations).GetMethod("IsOriginAllowed", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [origin, tenants, environment.Object, allowedOrigins])!;
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

    private static void SetPrivateStaticField(string fieldName, object? value)
    {
        var field = typeof(ApplicationConfigurations).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }

    private static void RemoveRegisteredObjectBsonSerializer()
    {
        var registry = BsonSerializer.SerializerRegistry;
        var cacheField = registry.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(f => f.FieldType == typeof(ConcurrentDictionary<Type, IBsonSerializer>));

        var cache = (ConcurrentDictionary<Type, IBsonSerializer>)cacheField.GetValue(registry)!;
        cache.TryRemove(typeof(object), out _);
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
