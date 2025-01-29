using Blocks.Genesis;
using TestDriver;

const string _serviceName = "Service-API-Test_One";

var blocksSecret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);
var builder = WebApplication.CreateBuilder(args);

ApplicationConfigurations.ConfigureApiEnv(builder, args);
ApplicationConfigurations.ConfigureKestrel(builder);

// Configure services
var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{
    Queues = new List<string> { "demo_queue" },
    Topics = new List<string> { "demo_topic" }
});

ApplicationConfigurations.ConfigureApi(services);

services.AddSingleton<IGrpcClient, GrpcClient>();

var app = builder.Build();


ApplicationConfigurations.ConfigureMiddleware(app);



app.Run();
