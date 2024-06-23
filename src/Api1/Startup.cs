using MongoDB.Driver;
using Serilog;
using StackExchange.Redis;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Api1
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddHttpClient();
            services.AddSingleton<IMongoClient, MongoClient>(sp => new MongoClient("mongodb://localhost:27017"));
            //services.AddSingleton<RabbitMQService>();
            //services.AddSingleton(sp => sp.GetRequiredService<RabbitMQService>().GetChannel());

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
                           .AddSource("YourServiceName")
                           .AddProcessor(new MongoDBTraceExporter());
                })
                .WithMetrics(builder =>
                {
                    builder.AddAspNetCoreInstrumentation()
                            .AddRuntimeInstrumentation()
                            .AddMeter("YourServiceName")
                            .AddMeter("Microsoft.AspNetCore.Hosting")
                            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                            .AddMeter("Microsoft.AspNetCore.Http.Connections")
                            .AddMeter("Microsoft.AspNetCore.Routing")
                            .AddMeter("Microsoft.AspNetCore.Diagnostics")
                            .AddMeter("Microsoft.AspNetCore.RateLimiting")
                            .AddProcessInstrumentation()
                           .AddReader(new PeriodicExportingMetricReader(new MongoDBMetricsExporter()));
                });

            
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();
            app.UseMiddleware<TraceContextMiddleware>();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }

}
