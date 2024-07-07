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

        public static void SetServiceName(string serviceName)
        {
            _serviceName = serviceName;
        }

        public static void ConfigureLog()
        {
            Log.Logger = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.With<TraceContextEnricher>()
                        .Enrich.WithEnvironmentName()
                        .WriteTo.Console()
                        .WriteTo.MongoDBWithDynamicCollection(_serviceName)
                        .CreateLogger();
        }

        public async static Task ConfigureServices(IServiceCollection services)
        {
            await LmtConfiguration.CreateCollectionAsync(_serviceName);

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

            services.AddSingleton<ICacheClient, RedisClient>();

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
