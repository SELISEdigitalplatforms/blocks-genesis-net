using Blocks.Genesis.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
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

            await LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, _serviceName);
            await LmtConfiguration.CreateCollectionForTraces(_blocksSecret.TraceConnectionString, _serviceName);
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
            services.AddSingleton<IMongoClient, MongoClient>(sp => new MongoClient(_blocksSecret.DatabaseConnectionString));

            services.AddOpenTelemetry()
                .WithTracing(builder =>
                {
                    builder.AddAspNetCoreInstrumentation()
                           .AddHttpClientInstrumentation()
                           .AddMongoDBInstrumentation()
                           .AddRedisInstrumentation()
                           .AddProcessor(new MongoDBTraceExporter(_serviceName, blocksSecret: _blocksSecret));
                });

            services.AddOpenTelemetry().WithMetrics(builder =>
            {
                builder.AddAspNetCoreInstrumentation()
                       .AddRuntimeInstrumentation()
                       .AddReader(new PeriodicExportingMetricReader(new MongoDBMetricsExporter(_serviceName, _blocksSecret)));
            });
        }

        public static void ConfigureAuth(IServiceCollection services)
        {
            services.JwtBearerAuthentication();
            services.AddControllers();
            services.AddHttpClient();
        }

        public static void ConfigureCustomMiddleware(IApplicationBuilder app)
        {
            app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
            app.UseMiddleware<TraceContextMiddleware>();
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
        }
    }
}
