using Blocks.Genesis.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
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

namespace Blocks.Genesis
{
    public static class ApplicationConfigurations
    {
        private static string _serviceName = string.Empty;
        private static IBlocksSecret _blocksSecret;
        private static BlocksSwaggerOptions _blocksSwaggerOptions;


        public static async Task<IBlocksSecret> ConfigureLogAndSecretsAsync(string serviceName, VaultType vaultType)
        {
            LoadDotEnvFile();
            _serviceName = serviceName;

            _blocksSecret = await BlocksSecret.ProcessBlocksSecret(vaultType);
            _blocksSecret.ServiceName = _serviceName;

            if (!string.IsNullOrWhiteSpace(_blocksSecret.TraceConnectionString))
            {
                LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, BlocksConstants.Miscellaneous);
            }
            if (!string.IsNullOrWhiteSpace(_blocksSecret.LogConnectionString))
            {
                LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, BlocksConstants.Miscellaneous);
                LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, serviceName);
            }
            if (!string.IsNullOrWhiteSpace(_blocksSecret.MetricConnectionString))
            {
                LmtConfiguration.CreateCollectionForMetrics(_blocksSecret.MetricConnectionString, BlocksConstants.Miscellaneous);
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
                });

            services.AddSingleton<IHttpService, HttpService>();

            ConfigureMessageClient(services, messageConfiguration).GetAwaiter().GetResult();

            services.AddHttpContextAccessor();
            services.AddHealthChecks();

            if (_blocksSwaggerOptions != null)
                services.AddBlocksSwagger(_blocksSwaggerOptions);

            services.AddSingleton<ICryptoService, CryptoService>();
            services.AddSingleton<IGrpcClientFactory, GrpcClientFactory>();
            services.AddHostedService<GenesisHealthPingBackgroundService>();
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
        /// HSTS -> CORS -> HealthChecks -> Swagger -> Routing -> API branch middleware
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

            var enableHsts = _blocksSecret.EnableHsts || app.Configuration.GetValue<bool>("EnableHsts");
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

            app.UseHealthChecks("/ping", new HealthCheckOptions
            {
                Predicate = _ => true,
                ResponseWriter = async (context, _) =>
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { message = $"pong from {_serviceName}" });
                }
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

        private static async Task ConfigureMessageClient(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            messageConfiguration.Connection ??= _blocksSecret.MessageConnectionString;
            messageConfiguration.ServiceName ??= _serviceName;

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
                    Console.WriteLine($"✓ Loaded environment variables from .env file");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load .env file: {ex.Message}");
            }
        }

        private static string GetAppSettingsFileName()
        {
            var currentEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            return string.IsNullOrWhiteSpace(currentEnvironment) ? "appsettings.json" : $"appsettings.{currentEnvironment}.json";
        }
    }
}