using Blocks.Genesis.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using System.Diagnostics;

namespace Blocks.Genesis.Configuration
{
    public static class ApplicationConfigurations
    {
        static string _serviceName = string.Empty;
        static IBlocksSecret _blocksSecret;

        public static async Task<IBlocksSecret> ConfigureLogAndSecretsAsync(string serviceName) // initiateConfiguration(serviceName) this will be called before builder
        {
            _serviceName = serviceName;
            var vaultConfig = GetVaultConfig();

            _blocksSecret = await BlocksSecret.ProcessBlocksSecret(CloudType.Azure, vaultConfig);
            _blocksSecret.ServiceName = _serviceName;

            // for tracing collection will be created by TenantIds. it will create from tenants caching
            // create miscellaneous tracing collection if not exist. it is for non tenant tracing.
            await LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, BlocksConstants.Miscellaneous);
            // Service wise collection creation for log
            await LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, _serviceName);
            // Service wise collection creation for metrics
            await LmtConfiguration.CreateCollectionForMetrics(_blocksSecret.MetricConnectionString, _serviceName);



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

        public static void ConfigureServices(IServiceCollection services)
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
        }

        public static void ConfigureAuth(IServiceCollection services)
        {
            services.JwtBearerAuthentication();
            services.AddControllers();
            services.AddHttpClient();
        }

        public static void ConfigureCustomMiddleware(IApplicationBuilder app)
        {
            app.UseMiddleware<TraceContextMiddleware>();
            app.UseMiddleware<TenantValidationMiddleware>();
            app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
        }

        public static void ConfigureAuthMiddleware(IApplicationBuilder app)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        public static void ConfigureMessageConsumerAsync(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            ConfigureMessage(services, messageConfiguration);
            services.AddHostedService<AzureMessageWorker>();
            services.AddSingleton<Consumer>();
            var routingTable = new RoutingTable(services);
            services.AddSingleton(routingTable);
        }

        public static void ConfigureMessage(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            services.AddSingleton(messageConfiguration);
            services.AddSingleton<IMessageClient, AzureMessageClient>();
            services.AddHostedService<HealthServiceWorker>();
        }
    }
}
