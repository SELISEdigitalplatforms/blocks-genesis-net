using Blocks.Genesis;



const string _serviceName = "Service-API-Test_Two";

var blocksSecret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);

var builder = WebApplication.CreateBuilder(args);

var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{
    Connection = blocksSecret.MessageConnectionString,
    Queues = new List<string> { "demo_queue_1" },
    Topics = new List<string> { "demo_topic_1" },
    ServiceName = _serviceName,
});
ApplicationConfigurations.ConfigureApi(services);

var app = builder.Build();

ApplicationConfigurations.ConfigureMiddleware(app);

app.Run();


