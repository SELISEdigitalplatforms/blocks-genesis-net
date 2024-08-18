using Blocks.Genesis;

const string _serviceName = "Service-API-Test_One";

var blocksSecret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);
var builder = WebApplication.CreateBuilder(args);

// Configure services
var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services);
ApplicationConfigurations.ConfigureAuth(services);

ApplicationConfigurations.ConfigureMessage(services, new MessageConfiguration
{
    Connection = blocksSecret.MessageConnectionString,
    Queues = new List<string> { "demo_queue" },
    Topics = new List<string> { "demo_topic" },
    ServiceName = blocksSecret.ServiceName,
});

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

ApplicationConfigurations.ConfigureCustomMiddleware(app);

app.UseRouting();

ApplicationConfigurations.ConfigureAuthMiddleware(app);

app.MapControllers();

app.Run();
