using Blocks.Genesis.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
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
    private static IBlocksSecret _blocksSecret = null!;
    private static BlocksSwaggerOptions? _blocksSwaggerOptions;

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
            .AddJsonFile(GetAppSettingsFileName(), optional: false, reloadOnChange: false)
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
            .AddJsonFile(GetAppSettingsFileName(), optional: false, reloadOnChange: false);

        var configuration = builder.Build();
        LmtConfigurationProvider.Initialize(configuration);
    }

    public static void ConfigureServices(IServiceCollection services, MessageConfiguration messageConfiguration)
    {
        services.AddSingleton(typeof(IBlocksSecret), _blocksSecret);
        services.AddSingleton<ICacheClient, RedisClient>();
        services.AddSingleton<ITenants, Tenants>();
        services.AddSingleton<IDbContextProvider, MongoDbContextProvider>();

        var objectSerializer = new ObjectSerializer(_ => true);
        try
        {
            BsonSerializer.RegisterSerializer(objectSerializer);
        }
        catch (BsonSerializationException)
        {
            // An ObjectSerializer has already been registered for this process
            // (e.g. re-entrant configuration or previous host bootstrap).
            // MongoDB's registry is process-global and rejects duplicates; the
            // existing registration is equivalent for our purposes.
        }

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

    public static void ConfigureApi(IServiceCollection services)
    {
        services.JwtBearerAuthentication();
        services.AddControllers();
        services.AddHttpClient();
        ConfigureRateLimiting(services);

        services.AddGrpc(options =>
        {
            options.Interceptors.Add<GrpcServerInterceptor>();
        });

        services.AddSingleton<ChangeControllerContext>();
        services.AddAntiforgery();
    }

    public static void ConfigureMiddleware(WebApplication app)
    {
        ConfigureMicroserviceMiddleware(app);
    }

    public static void ConfigureMicroserviceMiddleware(
        WebApplication app,
        Action<IApplicationBuilder>? beforeAuthentication = null,
        Action<IApplicationBuilder>? afterAuthorization = null,
        Action<WebApplication>? beforeControllerMapping = null,
        Action<WebApplication>? afterControllerMapping = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var enableHsts = _blocksSecret.EnableHsts || app.Configuration.GetValue<bool>("EnableHsts");
        if (enableHsts)
        {
            app.UseHsts();
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
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseRouting();

        ConfigureApiBranchMiddleware(
            app,
            beforeAuthentication: beforeAuthentication,
            afterAuthorization: afterAuthorization);

        app.UseAntiforgery();

        beforeControllerMapping?.Invoke(app);
        app.MapControllers();
        afterControllerMapping?.Invoke(app);
    }

    public static void ConfigureApiBranchMiddleware(
        IApplicationBuilder app,
        Action<IApplicationBuilder>? beforeAuthentication = null,
        Action<IApplicationBuilder>? afterAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<TenantValidationMiddleware>();
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
        messageConfiguration.ServiceName ??= _serviceName;

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
            var envFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".env");

            if (File.Exists(envFilePath))
            {
                DotNetEnv.Env.Load(envFilePath);
                Log.Information("Loaded environment variables from .env file");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load .env file");
        }
    }

    private static string GetAppSettingsFileName()
    {
        var currentEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
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
}
