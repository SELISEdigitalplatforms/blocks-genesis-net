using Blocks.Genesis.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
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

        public static async Task<IBlocksSecret> ConfigureLogAndSecretsAsync(string serviceName) // initiateConfiguration(serviceName) this will be called before builder
        {
            _serviceName = serviceName;

            var voltConfig = new Dictionary<string, string>
            {
              { "ClientSecret", "***REMOVED***" },
              { "KeyVaultUrl", "https://blocks-vault.vault.azure.net/" },
              { "TenantId", "5c6dd6a7-f0c7-4a32-8f7c-9ca7cebf6e87" },
              {"ClientId", "8d49c722-d33e-419e-8b42-8b543b573c4b" }
            };

            _blocksSecret = await BlocksSecret.ProcessBlocksSecret(CloudType.Azure, voltConfig);
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

        public  static void ConfigureServices(IServiceCollection services)
        {

            services.AddSingleton(typeof(IBlocksSecret), _blocksSecret);
            services.AddSingleton<ICacheClient, RedisClient>();

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

        public async static Task ConfigureMessageWorker(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            await ConfigerAzureServiceBus.ConfigerMessagesAsync(messageConfiguration);
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
