using Microsoft.AspNetCore.Builder;
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
        static IBlocksSecret _blocksSecret = null;

        public static void SetServiceName(string serviceName) // remove this
        {
            _serviceName = serviceName;
        }

        public static void ConfigureLog() // initiateConfiguration(serviceName) this will be called before builder
        {
            // Blocks secret load
            // set blocks secret in LmtConfiguration 

            //_serviceName = serviceName;

            Log.Logger = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.With<TraceContextEnricher>()
                        .Enrich.WithEnvironmentName()
                        .WriteTo.Console()
                        .WriteTo.MongoDBWithDynamicCollection(_serviceName)
                        .CreateLogger();

            //await LmtConfiguration.CreateCollectionForLogs(_blocksSecret.LogConnectionString, _serviceName);
            //await LmtConfiguration.CreateCollectionForTraces(_blocksSecret.TraceConnectionString, _serviceName);
            //await LmtConfiguration.CreateCollectionForMetrics(_blocksSecret.MetricConnectionString, _serviceName);
        }

        public async static Task ConfigureServices(IServiceCollection services)
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

            services.AddOpenTelemetry()
                .WithTracing(builder =>
                {
                    builder.AddAspNetCoreInstrumentation()
                           .AddHttpClientInstrumentation()
                           .AddMongoDBInstrumentation()
                           .AddRedisInstrumentation()
                           .AddProcessor(new MongoDBTraceExporter(_serviceName));
                });
            services.AddOpenTelemetry().WithMetrics(builder =>
            {
                builder.AddAspNetCoreInstrumentation()
                       .AddRuntimeInstrumentation()
                       .AddReader(new PeriodicExportingMetricReader(new MongoDBMetricsExporter(_serviceName)));
            });

        }

        public static void ConfigureAuth(IServiceCollection services)
        {
            services.JwtBearerAuthentication();
            services.AddControllers();
            services.AddHttpClient();
        }

        public static void ConfigureTraceContextMiddleware(IApplicationBuilder app)
        {
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
