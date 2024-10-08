using Blocks.Genesis;
using Blocks.Genesis.Configuration;

const string _serviceName = "Service-API-Test_One";

var blocksSecret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);
var builder = WebApplication.CreateBuilder(args);

ApplicationConfigurations.ConfigureAppConfigs(builder, args);

// Configure services
var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{
    Connection = blocksSecret.MessageConnectionString,
    Queues = new List<string> { "demo_queue" },
    Topics = new List<string> { "demo_topic" },
    ServiceName = blocksSecret.ServiceName,
});

ApplicationConfigurations.ConfigureApi(services);

var app = builder.Build();


ApplicationConfigurations.ConfigureMiddleware(app);

app.Run();
