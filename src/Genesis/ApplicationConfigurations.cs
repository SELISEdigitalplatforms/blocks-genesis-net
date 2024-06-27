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
        public static void ConfigureLog(string serviceName)
        {
            Log.Logger = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.With<TraceContextEnricher>()
                        .Enrich.WithEnvironmentName()
                        .WriteTo.Console()
                        .WriteTo.MongoDBWithDynamicCollection(serviceName)
                        .CreateLogger();

        }


        public static void ConfigureServices(IServiceCollection services, string serviceName)
        {
            LmtConfiguration.CreateCollectionAsync(serviceName);

            var objectSerializer = new ObjectSerializer(_ => true);
            BsonSerializer.RegisterSerializer(objectSerializer);

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });

            services.AddSingleton(new ActivitySource(serviceName));

            services.AddOpenTelemetry()
                .WithTracing(builder =>
                {
                    builder.AddAspNetCoreInstrumentation()
                           .AddHttpClientInstrumentation()
                           .AddMongoDBInstrumentation()
                           .AddRedisInstrumentation()
                           .AddSource(serviceName)
                           .AddProcessor(new MongoDBTraceExporter(serviceName));
                })
                .WithMetrics(builder =>
                {
                    builder.AddAspNetCoreInstrumentation()
                            .AddRuntimeInstrumentation()
                            .AddMeter(serviceName)
                            .AddMeter("Microsoft.AspNetCore.Hosting")
                            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                            .AddMeter("Microsoft.AspNetCore.Http.Connections")
                            .AddMeter("Microsoft.AspNetCore.Routing")
                            .AddMeter("Microsoft.AspNetCore.Diagnostics")
                            .AddMeter("Microsoft.AspNetCore.RateLimiting")
                            .AddProcessInstrumentation()
                           .AddReader(new PeriodicExportingMetricReader(new MongoDBMetricsExporter(serviceName)));
                });
        }

        public static void ConfigureTraceContextMiddleware(IApplicationBuilder app)
        {
            app.UseMiddleware<TraceContextMiddleware>();
        }

        public static void ConfigureMessageWorker(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            ConfigureMessage(services, messageConfiguration);

            services.AddHostedService<AzureMessageWorker>();


            services.AddSingleton<Consumer>();

            var routingTable = new RoutingTable(services);
            services.AddSingleton<RoutingTable>(routingTable);
        }

        public static void ConfigureMessage(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            ConfigerAzureServiceBus.ConfigerMessagesAsync(messageConfiguration);

            services.AddSingleton(messageConfiguration);

            services.AddSingleton<IMessageClient, AzureMessageClient>();
        }


    }
}
