using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using System.Diagnostics;
using System.Threading;

namespace Blocks.Genesis
{
    public static class ApplicationConfigurations
    {
        private static readonly AsyncLocal<ApplicationBootstrapState?> CurrentBootstrapState = new();
        private static readonly Lock BootstrapStateSyncRoot = new();
        private static ApplicationBootstrapState? LastBootstrapState;

        internal static ApplicationBootstrapState? GetBootstrapState()
        {
            lock (BootstrapStateSyncRoot)
            {
                return CurrentBootstrapState.Value ?? LastBootstrapState;
            }
        }


        public static async Task<IBlocksSecret> ConfigureLogAndSecretsAsync(
            string serviceName,
            SecretMode mode,
            Action<GenesisSecretOptions>? configure = null)
        {
            LoadDotEnvFile();

            var options = new GenesisSecretOptions { Mode = mode };
            configure?.Invoke(options);

            var blocksSecret = await BlocksSecret.ProcessBlocksSecret(options);
            blocksSecret.ServiceName = serviceName;

            CurrentBootstrapState.Value = new ApplicationBootstrapState(
                serviceName,
                blocksSecret,
                null);
            lock (BootstrapStateSyncRoot)
            {
                LastBootstrapState = CurrentBootstrapState.Value;
            }

            if (!string.IsNullOrWhiteSpace(blocksSecret.LogConnectionString))
            {
                LmtConfiguration.CreateCollectionForLogs(blocksSecret.LogConnectionString, serviceName);
            }


            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.With<TraceContextEnricher>()
                .Enrich.WithEnvironmentName()
                .WriteTo.Console()
                .WriteTo.MongoDBWithDynamicCollection(serviceName, blocksSecret)
                .CreateLogger();

            return blocksSecret;
        }

        public static void ConfigureKestrel(WebApplicationBuilder builder)
        {
            var httpPort = Environment.GetEnvironmentVariable("HTTP1_PORT") ?? "5000";
            var http2Port = Environment.GetEnvironmentVariable("HTTP2_PORT") ?? "5001";

            builder.WebHost.ConfigureKestrel(options =>
            {
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


            var swaggerOptions = builder.Configuration.GetSection("SwaggerOptions").Get<BlocksSwaggerOptions>();
            if (GetBootstrapState() != null)
            {
                CurrentBootstrapState.Value = GetBootstrapState()! with { SwaggerOptions = swaggerOptions };
                lock (BootstrapStateSyncRoot)
                {
                    LastBootstrapState = CurrentBootstrapState.Value;
                }
            }

            var blocksProjectKey = builder.Configuration.GetValue<string>("BlocksProjectKey");
            Log.Information("Blocks project key configured: {BlocksProjectKey}", blocksProjectKey);
            BlocksConstants.SetBlocksProjectKey(blocksProjectKey);
            // Initialize LMT configuration provider
            LmtConfigurationProvider.Initialize(builder.Configuration);

        }

        public static void ConfigureWorkerEnv(IConfigurationBuilder builder, string[] args)
        {
            builder
                .AddCommandLine(args)
                .AddEnvironmentVariables()
                .AddJsonFile(GetAppSettingsFileName(), optional: false, reloadOnChange: false);

            // Initialize LMT configuration provider
            var configuration = builder.Build();
            LmtConfigurationProvider.Initialize(configuration);
        }

        public static void ConfigureServices(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            var state = GetRequiredBootstrapState();
            var activitySource = new ActivitySource(state.ServiceName);
            var appMemoryCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = 300,
                CompactionPercentage = 0.25
            });
            var traceEnsureMemoryCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = 1000,
                CompactionPercentage = 0.25
            });
            var cacheClient = new RedisClient(state.BlocksSecret, activitySource);
            var ensurer = new TraceCollectionEnsurer(state.BlocksSecret, cacheClient, traceEnsureMemoryCache);

            // Shared L1 cache instance for tenant state and mongo client cache.
            services.AddSingleton<IMemoryCache>(appMemoryCache);

            services.AddSingleton(state);
            services.AddSingleton(typeof(IBlocksSecret), state.BlocksSecret);
            services.AddSingleton<ICacheClient>(cacheClient);
            services.AddSingleton<ITenants, Tenants>();
            services.AddSingleton<ITraceCollectionEnsurer>(ensurer);
            services.AddSingleton<IDbContextProvider, MongoDbContextProvider>();
            services.AddSingleton<ITenantManagementService>(sp => new TenantManagementService(
                sp.GetRequiredService<ILogger<TenantManagementService>>(),
                state.BlocksSecret,
                cacheClient,
                ensurer));

            var objectSerializer = new ObjectSerializer(_ => true);
            BsonSerializer.RegisterSerializer(objectSerializer);

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });

            services.AddSingleton(activitySource);

            services.AddOpenTelemetry()
                .WithTracing(tracingBuilder =>
                {
                    tracingBuilder
                        .SetSampler(new AlwaysOnSampler())
                        .AddAspNetCoreInstrumentation()
                        .AddProcessor(new MongoDBTraceExporter(
                            state.ServiceName,
                            blocksSecret: state.BlocksSecret,
                            ensurer: ensurer));
                });

            services.AddSingleton<IHttpService, HttpService>();

            ConfigureMessageClient(services, messageConfiguration, state).GetAwaiter().GetResult();

            CurrentBootstrapState.Value = null;

            services.AddHttpContextAccessor();

            if (state.SwaggerOptions != null)
                services.AddBlocksSwagger(state.SwaggerOptions);

            services.AddSingleton<ICryptoService, CryptoService>();
            services.AddSingleton<IGrpcClientFactory, GrpcClientFactory>();
        }

        public static void ConfigureApi(IServiceCollection services)
        {
            services.JwtBearerAuthentication();
            services.AddControllers();
            services.AddHttpClient();

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

        /// <summary>
        /// Configures the full middleware pipeline for API-only microservice applications.
        /// </summary>
        /// <param name="app">The web application pipeline.</param>
        /// <param name="beforeAuthentication">Optional hook to add middleware between exception handling and authentication.</param>
        /// <param name="afterAuthorization">Optional hook to add middleware after authorization.</param>
        /// <param name="beforeControllerMapping">Optional hook to add middleware before controller endpoint mapping.</param>
        /// <param name="afterControllerMapping">Optional hook to run additional setup after controller endpoint mapping.</param>
        /// <remarks>
        /// Sequence:
        /// HSTS -> CORS -> Ping endpoint -> Swagger -> Routing -> API branch middleware
        /// -> beforeControllerMapping -> MapControllers -> afterControllerMapping -> Antiforgery.
        ///
        /// Example:
        /// ApplicationConfigurations.ConfigureMicroserviceMiddleware(
        ///     app,
        ///     beforeAuthentication: p => p.UseMiddleware&lt;RequestAuditMiddleware&gt;(),
        ///     afterAuthorization: p => p.UseMiddleware&lt;PermissionTelemetryMiddleware&gt;(),
        ///     beforeControllerMapping: a => { },
        ///     afterControllerMapping: a => { });
        /// </remarks>
        public static void ConfigureMicroserviceMiddleware(
            WebApplication app,
            Action<IApplicationBuilder>? beforeAuthentication = null,
            Action<IApplicationBuilder>? afterAuthorization = null,
            Action<WebApplication>? beforeControllerMapping = null,
            Action<WebApplication>? afterControllerMapping = null)
        {
            ArgumentNullException.ThrowIfNull(app);

            var state = app.Services.GetRequiredService<ApplicationBootstrapState>();

            var enableHsts = state.BlocksSecret.EnableHsts || app.Configuration.GetValue<bool>("EnableHsts");
            if (enableHsts)
            {
                app.UseHsts();
            }

            app.UseCors(corsPolicyBuilder =>
                corsPolicyBuilder
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .SetIsOriginAllowed(_ => true)
                    .AllowCredentials()
                    .SetPreflightMaxAge(TimeSpan.FromDays(365)));

            app.MapGet("/ping", () => Results.Json(new { message = $"pong from {state.ServiceName}" }));

            if (state.SwaggerOptions != null)
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseRouting();

            ConfigureApiBranchMiddleware(
                app,
                beforeAuthentication: beforeAuthentication,
                afterAuthorization: afterAuthorization);

            beforeControllerMapping?.Invoke(app);
            app.MapControllers();
            afterControllerMapping?.Invoke(app);
            app.UseAntiforgery();
        }

        /// <summary>
        /// Configures the reusable API security middleware chain for branch pipelines (for example, inside UseWhen for /api).
        /// </summary>
        /// <param name="app">The application builder for the target branch pipeline.</param>
        /// <param name="beforeAuthentication">Optional hook to insert middleware before authentication.</param>
        /// <param name="afterAuthorization">Optional hook to insert middleware after authorization.</param>
        /// <remarks>
        /// Sequence:
        /// TenantValidationMiddleware -> GlobalExceptionHandlerMiddleware -> beforeAuthentication
        /// -> UseAuthentication -> UseAuthorization -> afterAuthorization.
        ///
        /// Example:
        /// app.UseWhen(
        ///     ctx => ctx.Request.Path.StartsWithSegments("/api"),
        ///     api => ApplicationConfigurations.ConfigureApiBranchMiddleware(
        ///         api,
        ///         beforeAuthentication: p => p.UseMiddleware&lt;CustomPreAuthMiddleware&gt;(),
        ///         afterAuthorization: p => p.UseMiddleware&lt;CustomPostAuthMiddleware&gt;()));
        /// </remarks>
        public static void ConfigureApiBranchMiddleware(
            IApplicationBuilder app,
            Action<IApplicationBuilder>? beforeAuthentication = null,
            Action<IApplicationBuilder>? afterAuthorization = null)
        {
            ArgumentNullException.ThrowIfNull(app);

            app.UseMiddleware<TenantValidationMiddleware>();
            app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

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

        private static async Task ConfigureMessageClient(
            IServiceCollection services,
            MessageConfiguration messageConfiguration,
            ApplicationBootstrapState state)
        {
            if (messageConfiguration.AzureServiceBusConfiguration != null &&
                messageConfiguration.RabbitMqConfiguration != null)
            {
                throw new InvalidOperationException("Configure either Azure Service Bus or RabbitMQ for IMessageClient, not both.");
            }

            messageConfiguration.Connection ??= state.BlocksSecret.MessageConnectionString;
            messageConfiguration.ServiceName ??= state.ServiceName;

            services.AddSingleton(messageConfiguration);

            if (messageConfiguration.AzureServiceBusConfiguration != null)
            {
                services.AddSingleton<IMessageClient, AzureMessageClient>();
                await ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(messageConfiguration);
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

        private static ApplicationBootstrapState GetRequiredBootstrapState()
        {
            return CurrentBootstrapState.Value ?? throw new InvalidOperationException(
                "Call ConfigureLogAndSecretsAsync before configuring application services or middleware.");
        }

        private static string GetAppSettingsFileName()
        {
            var currentEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            return string.IsNullOrWhiteSpace(currentEnvironment) ? "appsettings.json" : $"appsettings.{currentEnvironment}.json";
        }

        internal sealed record ApplicationBootstrapState(
            string ServiceName,
            IBlocksSecret BlocksSecret,
            BlocksSwaggerOptions? SwaggerOptions);
    }
}