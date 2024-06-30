using Blocks.Genesis;
using MongoDB.Driver;

const string _serviceName = "Service-API-Test_One";


ApplicationConfigurations.SetServiceName(_serviceName);
ApplicationConfigurations.ConfigureLog();

var builder = WebApplication.CreateBuilder(args);

// Configure services
var services = builder.Services;
services.AddControllers();
services.AddHttpClient();

services.AddSingleton<IMongoClient, MongoClient>(sp => new MongoClient("mongodb://localhost:27017"));

ApplicationConfigurations.ConfigureServices(services);

ApplicationConfigurations.ConfigureMessage(services, new MessageConfiguration
{
    Connection = "Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***",
    Queues = new List<string> { "demo_queue" },
    Topics = new List<string> { "demo_topic" },
    ServiceName = _serviceName,
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
