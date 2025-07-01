using Blocks.Genesis;
using TestDriver;

const string _serviceName = "Service-API-Test_One";

await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName, VaultType.Azure);
var builder = WebApplication.CreateBuilder(args);

ApplicationConfigurations.ConfigureApiEnv(builder, args);
var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{
    AzureServiceBusConfiguration = new()
    {
        Queues = new List<string> { "demo_queue" },
        Topics = new List<string> { "demo_topic_1" },
    },
});

ApplicationConfigurations.ConfigureApi(services);
services.AddSingleton<IGrpcClient, GrpcClient>();
var app = builder.Build();
ApplicationConfigurations.ConfigureMiddleware(app);

await app.RunAsync();
