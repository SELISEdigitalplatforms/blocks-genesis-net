using Blocks.Genesis;
using MongoDB.Driver;

namespace Api1
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ApplicationConfigurations.ConfigureLog("Service-API-Test_One");

            var builder = WebApplication.CreateBuilder(args);

            // Configure services
            var services = builder.Services;
            services.AddControllers();
            services.AddHttpClient();

            services.AddSingleton<IMongoClient, MongoClient>(sp => new MongoClient("mongodb://localhost:27017"));

            ApplicationConfigurations.ConfigureServices(services, "Service-API-Test_One");

            ApplicationConfigurations.ConfigureMessage(services, new MessageConfiguration
            {
                Connection = "Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***",
                Queues = new List<string> { "demo_queue" },
                Topics = new List<string> { "demo_topic" }
            });

            var app = builder.Build();

            // Configure middleware
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            ApplicationConfigurations.ConfigureTraceContextMiddleware(app);

            app.UseRouting();

            app.MapControllers();

            app.Run();
        }
    }
}
