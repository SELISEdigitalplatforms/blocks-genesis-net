using Blocks.Genesis.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using System.Diagnostics;
using System.Threading.RateLimiting;

namespace Blocks.Genesis;

public static class ApplicationConfigurations
{
    private static string _serviceName = string.Empty;
    private static string? _serviceAccessResourceName;
    private static IBlocksSecret _blocksSecret = null!;
    private static BlocksSwaggerOptions? _blocksSwaggerOptions;

    internal static string? ServiceAccessResourceName => _serviceAccessResourceName;

    public static async Task<IBlocksSecret> ConfigureLogAndSecretsAsync(string serviceName, VaultType vaultType)
    {
        LoadDotEnvFile();
        _serviceName = serviceName;

        _blocksSecret = await BlocksSecret.ProcessBlocksSecret(vaultType);
        _blocksSecret.ServiceName = _serviceName;

        if (!string.IsNullOrWhiteSpace(_blocksSecret.LogConnectionString))
        {
            LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, serviceName);
        }

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<TraceContextEnricher>()
            .Enrich.WithEnvironmentName()
            .WriteTo.Console()
            .WriteTo.MongoDBWithDynamicCollection(_serviceName, _blocksSecret)
            .CreateLogger();

        return _blocksSecret;
    }

    public static VaultType ResolveVaultType(VaultType defaultVaultType = VaultType.Azure)
    {
        LoadDotEnvFile();

        var configuredVaultType = Environment.GetEnvironmentVariable("BLOCKS_VAULT_TYPE");
        if (!string.IsNullOrWhiteSpace(configuredVaultType) &&
            Enum.TryParse<VaultType>(configuredVaultType, true, out var parsedVaultType))
        {
            return parsedVaultType;
        }

        return defaultVaultType;
    }

    public static void ConfigureKestrel(WebApplicationBuilder builder)
    {
        var httpPort = Environment.GetEnvironmentVariable("HTTP1_PORT") ?? "5000";
        var http2Port = Environment.GetEnvironmentVariable("HTTP2_PORT") ?? "5001";

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;

            options.ListenAnyIP(int.Parse(httpPort), listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
            });

            options.ListenAnyIP(int.Parse(http2Port), listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });
    }

    public static void ConfigureApiEnv(IHostApplicationBuilder builder, string[] args)
    {
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile(GetEnvironmentAppSettingsFileName(), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);

        _blocksSwaggerOptions = builder.Configuration.GetSection("SwaggerOptions").Get<BlocksSwaggerOptions>();
        LmtConfigurationProvider.Initialize(builder.Configuration);
    }

    public static void ConfigureWorkerEnv(IConfigurationBuilder builder, string[] args)
    {
        builder
            .AddCommandLine(args)
            .AddEnvironmentVariables()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile(GetEnvironmentAppSettingsFileName(), optional: true, reloadOnChange: false);

        var configuration = builder.Build();
        LmtConfigurationProvider.Initialize(configuration);
    }

    public static void ConfigureServices(IServiceCollection services, MessageConfiguration messageConfiguration)
    {
        EnsureRequiredSecretsConfigured();

        services.AddSingleton(typeof(IBlocksSecret), _blocksSecret);
        services.AddSingleton<ICacheClient, RedisClient>();
        services.AddSingleton<ITenants, Tenants>();
        services.AddSingleton<IDbContextProvider, MongoDbContextProvider>();

        var objectSerializer = new ObjectSerializer(_ => true);
        BsonSerializer.RegisterSerializer(objectSerializer);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog();
        });

        services.AddSingleton(new ActivitySource(_serviceName));

        services.AddOpenTelemetry()
            .WithTracing(tracingBuilder =>
            {
                tracingBuilder
                    .SetSampler(new AlwaysOnSampler())
                    .AddAspNetCoreInstrumentation()
                    .AddProcessor(new MongoDBTraceExporter(_serviceName, blocksSecret: _blocksSecret));
            })
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation();
            });

        services.AddSingleton<IHttpService, HttpService>();

        ConfigureMessageClient(services, messageConfiguration).GetAwaiter().GetResult();

        services.AddHttpContextAccessor();
        services.AddSingleton<IMongoClient>(_ => new MongoClient(_blocksSecret.DatabaseConnectionString));
        var mongoDatabaseName = MongoUrl.Create(_blocksSecret.DatabaseConnectionString).DatabaseName ?? "admin";
        services.AddHealthChecks()
            .AddMongoDb(
                sp => sp.GetRequiredService<IMongoClient>(),
                _ => mongoDatabaseName,
                name: "mongodb",
                tags: ["ready"])
            .AddRedis(_blocksSecret.CacheConnectionString, name: "redis", tags: ["ready"]);

        if (_blocksSwaggerOptions != null)
        {
            services.AddBlocksSwagger(_blocksSwaggerOptions);
        }

        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<IGrpcClientFactory, GrpcClientFactory>();
        services.AddHostedService<GenesisHealthPingBackgroundService>();
    }

    private static void EnsureRequiredSecretsConfigured()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(_blocksSecret.DatabaseConnectionString))
            missing.Add(nameof(_blocksSecret.DatabaseConnectionString));

        if (string.IsNullOrWhiteSpace(_blocksSecret.CacheConnectionString))
            missing.Add(nameof(_blocksSecret.CacheConnectionString));

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Missing required Blocks secret configuration: {string.Join(", ", missing)}. " +
            "Provide values via vault or environment (.env)."
        );
    }

    public static void ConfigureApi(
        IServiceCollection services,
        string serviceName,
        string? apiRoutePrefix = "api",
        string? serviceAccessResourceName = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name is required.", nameof(serviceName));
        }

        _serviceName = serviceName.Trim();
        _serviceAccessResourceName = string.IsNullOrWhiteSpace(serviceAccessResourceName)
            ? _serviceName
            : serviceAccessResourceName.Trim();

        services.JwtBearerAuthentication();

        var normalizedPrefix = NormalizeApiRoutePrefixValue(apiRoutePrefix);
        services.AddControllers(options =>
        {
            options.Conventions.Insert(0, new ApiRoutePrefixConvention(normalizedPrefix));
            options.Filters.Add<ProtectedEndPointResourceFilter>();
        });

        services.AddHttpClient();
        ConfigureRateLimiting(services);

        services.AddGrpc(options =>
        {
            options.Interceptors.Add<GrpcServerInterceptor>();
        });
        services.AddAntiforgery();
    }

    public static void ConfigureMiddleware(WebApplication app, IEnumerable<string>? tenantValidationPrefixes = null)
    {
        ConfigureMicroserviceMiddleware(app, tenantValidationPrefixes: tenantValidationPrefixes);
    }

    public static void ConfigureMicroserviceMiddleware(
        WebApplication app,
        Action<IApplicationBuilder>? beforeAuthentication = null,
        Action<IApplicationBuilder>? afterAuthorization = null,
        Action<WebApplication>? beforeControllerMapping = null,
        Action<WebApplication>? afterControllerMapping = null,
        IEnumerable<string>? tenantValidationPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var enableHsts = _blocksSecret.EnableHsts || app.Configuration.GetValue<bool>("EnableHsts");
        if (enableHsts)
        {
            app.UseHsts();
        }

        var pathBase = NormalizePathBase(_blocksSwaggerOptions?.PathBase);
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            app.UsePathBase(pathBase);
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<RequestMetricsMiddleware>();

        var tenants = app.Services.GetRequiredService<ITenants>();
        var allowedCorsOrigins = ParseAllowedCorsOrigins();

        app.UseCors(corsPolicyBuilder =>
            corsPolicyBuilder
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .SetPreflightMaxAge(TimeSpan.FromHours(2))
                .SetIsOriginAllowed(origin => IsOriginAllowed(origin, tenants, app.Environment, allowedCorsOrigins)));



        app.UseHealthChecks("/ping", new HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = HealthCheckResponseWriter.WriteAsync
        });

        app.UseHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = HealthCheckResponseWriter.WriteAsync
        });

        app.UseHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = HealthCheckResponseWriter.WriteAsync
        });

        if (_blocksSwaggerOptions != null)
        {
            app.UseSwagger(options =>
            {
                if (!string.IsNullOrWhiteSpace(pathBase))
                {
                    options.PreSerializeFilters.Add((swaggerDoc, _) =>
                    {
                        swaggerDoc.Servers =
                        [
                            new OpenApiServer { Url = pathBase }
                        ];
                    });
                }
            });

            app.UseSwaggerUI(options =>
            {
                var endpointUrl = _blocksSwaggerOptions.EndpointUrl;
                if (!string.IsNullOrWhiteSpace(pathBase) && endpointUrl.StartsWith('/'))
                {
                    endpointUrl = $"{pathBase}{endpointUrl}";
                }

                var version = string.IsNullOrWhiteSpace(_blocksSwaggerOptions.Version)
                    ? "v1"
                    : _blocksSwaggerOptions.Version;

                options.SwaggerEndpoint(endpointUrl, $"{_blocksSwaggerOptions.Title} {version}");
            });
        }

        app.UseRouting();

        ConfigureApiBranchMiddleware(
            app,
            beforeAuthentication: beforeAuthentication,
            afterAuthorization: afterAuthorization,
            tenantValidationPrefixes: tenantValidationPrefixes);

        app.UseAntiforgery();

        beforeControllerMapping?.Invoke(app);
        app.MapControllers();
        afterControllerMapping?.Invoke(app);
    }

    public static void ConfigureApiBranchMiddleware(
        IApplicationBuilder app,
        Action<IApplicationBuilder>? beforeAuthentication = null,
        Action<IApplicationBuilder>? afterAuthorization = null,
        IEnumerable<string>? tenantValidationPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var tenantPrefixes = tenantValidationPrefixes?.ToArray() ?? Array.Empty<string>();
        app.UseMiddleware<TenantValidationMiddleware>((object)tenantPrefixes);
        app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
        app.UseRateLimiter();

        beforeAuthentication?.Invoke(app);

        app.UseAuthentication();
        app.UseAuthorization();

        afterAuthorization?.Invoke(app);
    }

    public static void ConfigureWorker(IServiceCollection services, MessageConfiguration messageConfiguration)
    {
        ConfigureServices(services, messageConfiguration);

        if (messageConfiguration.AzureServiceBusConfiguration != null)
        {
            services.AddHostedService<AzureMessageWorker>();
        }

        if (messageConfiguration.RabbitMqConfiguration != null)
        {
            services.AddHostedService<RabbitMessageWorker>();
        }

        services.AddSingleton<Consumer>();
        var routingTable = new RoutingTable(services);
        services.AddSingleton(routingTable);
    }

    private static async Task ConfigureMessageClient(IServiceCollection services, MessageConfiguration messageConfiguration)
    {
        messageConfiguration.Connection ??= _blocksSecret.MessageConnectionString;
        // Note: messageConfiguration.ServiceName should be set by the caller before calling this method

        services.AddSingleton(messageConfiguration);

        if (messageConfiguration.AzureServiceBusConfiguration != null)
        {
            services.AddSingleton<IMessageClient, AzureMessageClient>();
            await ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync(messageConfiguration);
        }

        if (messageConfiguration.RabbitMqConfiguration != null)
        {
            services.AddSingleton<IRabbitMqService, RabbitMqService>();
            services.AddSingleton<IMessageClient, RabbitMessageClient>();
        }
    }

    private static void LoadDotEnvFile()
    {
        try
        {
            var envFilePath = FindDotEnvFile();

            if (!string.IsNullOrWhiteSpace(envFilePath))
            {
                DotNetEnv.Env.Load(envFilePath);
                Log.Information("Loaded environment variables from .env file: {EnvFilePath}", envFilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load .env file");
        }
    }

    private static string? FindDotEnvFile()
    {
        var currentDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
        var directories = new List<DirectoryInfo>();

        while (currentDirectory is not null)
        {
            directories.Add(currentDirectory);
            currentDirectory = currentDirectory.Parent;
        }

        directories.Reverse();

        foreach (var directory in directories)
        {
            var rootEnvPath = Path.Combine(directory.FullName, ".env");
            if (File.Exists(rootEnvPath))
            {
                return rootEnvPath;
            }
        }

        foreach (var directory in directories)
        {
            var nestedEnvPath = Path.Combine(directory.FullName, "server", ".env");
            if (File.Exists(nestedEnvPath))
            {
                return nestedEnvPath;
            }
        }

        return null;
    }

    private static string GetEnvironmentAppSettingsFileName()
    {
        var currentEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.IsNullOrWhiteSpace(currentEnvironment) ? "appsettings.json" : $"appsettings.{currentEnvironment}.json";
    }

    private static List<string> ParseAllowedCorsOrigins()
    {
        return (_blocksSecret?.AllowedCorsOrigins ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsOriginAllowed(string? origin, ITenants tenants, IHostEnvironment environment, IReadOnlyCollection<string> allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (environment.IsDevelopment() &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("127.0.0.1")))
        {
            return true;
        }

        if (allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedOrigin = uri.GetLeftPart(UriPartial.Authority);
        return tenants.GetTenantByApplicationDomain(normalizedOrigin) != null;
    }

    private static void ConfigureRateLimiting(IServiceCollection services)
    {
        var permitLimit = int.TryParse(
            Environment.GetEnvironmentVariable("BLOCKS_RATE_LIMIT_PER_MINUTE"),
            out var configuredPermitLimit)
            ? Math.Max(1, configuredPermitLimit)
            : 120;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var tenantId = httpContext.Request.Headers["tenant-id"].FirstOrDefault();
                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var partitionKey = string.IsNullOrWhiteSpace(tenantId)
                    ? $"ip:{remoteIp}"
                    : $"tenant:{tenantId}";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });
        });
    }

    private static string NormalizePathBase(string? rawPathBase)
    {
        if (string.IsNullOrWhiteSpace(rawPathBase))
        {
            return string.Empty;
        }

        var trimmed = rawPathBase.Trim();
        trimmed = trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
        return trimmed.TrimEnd('/');
    }

    public static string NormalizeApiRoutePrefixValue(string? apiRoutePrefix)
    {
        if (string.IsNullOrWhiteSpace(apiRoutePrefix))
        {
            return "api";
        }

        var trimmed = apiRoutePrefix.Trim().Trim('/');

        if (string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "api" : trimmed;
    }

}
