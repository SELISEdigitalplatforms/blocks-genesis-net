using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

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
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });

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

        public static async Task ConfigureMessageWorker(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            await ConfigureMessage(services, messageConfiguration);

            services.AddHostedService<AzureMessageWorker>();
        }

        public static async Task ConfigureMessage(IServiceCollection services, MessageConfiguration messageConfiguration)
        {
            await ConfigerAzureServiceBus.ConfigerMessagesAsync(messageConfiguration);

            services.AddSingleton(_ =>
            {
                return messageConfiguration;
            });

            services.AddSingleton<IMessageClient, AzureMessageClient>();
        }


    }
}
