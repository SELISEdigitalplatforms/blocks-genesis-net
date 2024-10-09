using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        static string _serviceName = string.Empty;
        static IBlocksSecret _blocksSecret;
        static BlocksSwaggerOptions _blocksSwaggerOptions;

        public static async Task<IBlocksSecret> ConfigureLogAndSecretsAsync(string serviceName) // initiateConfiguration(serviceName) this will be called before builder
        {
            _serviceName = serviceName;
            var vaultConfig = GetVaultConfig();

            _blocksSecret = await BlocksSecret.ProcessBlocksSecret(CloudType.Azure, vaultConfig);
            _blocksSecret.ServiceName = _serviceName;

            // for tracing collection will be created by TenantIds. it will create from tenants caching
            // create miscellaneous tracing collection if not exist. it is for non tenant tracing.
            LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, BlocksConstants.Miscellaneous);
            // Service wise collection creation for log
            LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, _serviceName);
            // Service wise collection creation for metrics
            LmtConfiguration.CreateCollectionForMetrics(_blocksSecret.MetricConnectionString, _serviceName);



            Log.Logger = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.With<TraceContextEnricher>()
                        .Enrich.WithEnvironmentName()
                        .WriteTo.Console()
                        .WriteTo.MongoDBWithDynamicCollection(_serviceName, _blocksSecret)
                        .CreateLogger();

            return _blocksSecret;
        }

        private static Dictionary<string, string> GetVaultConfig()
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var keyVaultConfig = new Dictionary<string, string>();
            configuration.GetSection("KeyVault").Bind(keyVaultConfig);

            return keyVaultConfig;
        }

        public static void ConfigureAppConfigs(WebApplicationBuilder builder, string[] args)
        {
            builder.Configuration
            .AddCommandLine(args)
            .AddEnvironmentVariables()
            .AddJsonFile(GetAppSettingsFileName(), optional: false, reloadOnChange: false);

            _blocksSwaggerOptions = builder.Configuration.GetSection("SwaggerOptions").Get<BlocksSwaggerOptions>();
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
                .WithTracing(builder =>
                {
                    builder.SetSampler(new AlwaysOnSampler())
                    .AddAspNetCoreInstrumentation()
                    .AddProcessor(new MongoDBTraceExporter(_serviceName, blocksSecret: _blocksSecret));
                });

            services.AddOpenTelemetry().WithMetrics(builder =>
            {
                builder.AddAspNetCoreInstrumentation()
                       .AddRuntimeInstrumentation()
                       .AddReader(new PeriodicExportingMetricReader(new MongoDBMetricsExporter(_serviceName, _blocksSecret)));
            });

            services.AddSingleton<IHttpService, HttpService>();

            ConfigureMessageClient(services, messageConfiguration);

            services.AddHealthChecks();

            if (_blocksSwaggerOptions != null) services.AddBlocksSwagger(_blocksSwaggerOptions);
        }

        public static void ConfigureApi(IServiceCollection services)
        {
            services.JwtBearerAuthentication();
            services.AddControllers();
            services.AddHttpClient();
        }

        public static void ConfigureMiddleware(WebApplication app)
        {
            var enableHsts = app.Configuration.GetValue<bool>("EnableHsts");
            if (enableHsts)
            {
                app.UseHsts();
            }

            app.UseHealthChecks("/ping", new HealthCheckOptions
            {
                Predicate = _ => true,
                ResponseWriter = async (context, _) =>
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { message = $"pong from {_serviceName}" });
                }
            });

            // Enable CORS with specified configuration
            app.UseCors(corsPolicyBuilder =>
                corsPolicyBuilder
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .SetIsOriginAllowed(origin => true)
                    .AllowCredentials()
                    .SetPreflightMaxAge(TimeSpan.FromDays(365)));

            if (_blocksSwaggerOptions != null)
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }


            // Custom middlewares
            app.UseMiddleware<TraceContextMiddleware>();
            app.UseMiddleware<TenantValidationMiddleware>();
            app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

            // Authentication and Authorization
            app.UseAuthentication();
            app.UseAuthorization();

            // Routing must be called before mapping endpoints
            app.UseRouting();

            // Map controllers or endpoints
            app.MapControllers();
        }

        public static void ConfigureWorker(IServiceCollection services)
        {
            services.AddHostedService<AzureMessageWorker>();
            services.AddSingleton<Consumer>();
            var routingTable = new RoutingTable(services);
            services.AddSingleton(routingTable);
        }

        private static void ConfigureMessageClient(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            messageConfiguration.Connection = string.IsNullOrWhiteSpace(messageConfiguration.Connection) ? _blocksSecret.MessageConnectionString : messageConfiguration.Connection;
            messageConfiguration.ServiceName = string.IsNullOrWhiteSpace(messageConfiguration.ServiceName) ? _serviceName : messageConfiguration.ServiceName;
            services.AddSingleton(messageConfiguration);
            services.AddSingleton<IMessageClient, AzureMessageClient>();
            services.AddHostedService<HealthServiceWorker>();
        }

        private static string GetAppSettingsFileName()
        {
            var currentEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            return string.IsNullOrWhiteSpace(currentEnvironment) ? "appsettings.json" : $"appsettings.{currentEnvironment}.json";
        }
    }
}
