using Serilog;
using Serilog.Events;
using Serilog.Templates;
using Serilog.Templates.Themes;

namespace Api
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Apis")
            .Enrich.WithEnvironmentName()
            .Enrich.WithProcessId()
            .WriteTo.Console(new ExpressionTemplate(
            // Include trace and span ids when present.
            "[{@t:yyyy-mm-dd HH:mm:ss} {@l}{#if @tr is not null} {@tr}:{@sp} {#end}] {@m}\n{@x}",
            theme: TemplateTheme.Code), LogEventLevel.Verbose)
            .WriteTo.MongoDB("mongodb://localhost:27017/logs", collectionName: "Apis")
            .CreateLogger();

            //using (new ActivityListenerConfiguration().Instrument.AspNetCoreRequests().TraceToSharedLogger())
            //{

            //}

            try
            {
                Log.Information("Starting up");
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application start-up failed");
            }
            finally
            {
                Log.CloseAndFlush();
            }


        }
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
