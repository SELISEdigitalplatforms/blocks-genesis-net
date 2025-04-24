using Blocks.Genesis;
using GrpcServiceTestTemp.Services;

const string _serviceName = "Service-API-Test_Two";
var blocksSecret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);
var builder = WebApplication.CreateBuilder(args);
ApplicationConfigurations.ConfigureApiEnv(builder, args);
//ApplicationConfigurations.ConfigureKestrel(builder);

var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{
    Connection = blocksSecret.MessageConnectionString,
    RabbitMqConfiguration = new()
    {
        ConsumerSubscriptions = new()
        {
            ConsumerSubscription.BindToQueue("test_from_cloud_queue_1", 2),
            ConsumerSubscription.BindToQueueViaExchange("test_from_cloud_queue_1", "test_from_cloud_exchange_1", 2),
        }
    },
    ServiceName = _serviceName,
});

ApplicationConfigurations.ConfigureApi(services);
var app = builder.Build();
ApplicationConfigurations.ConfigureMiddleware(app);

app.MapGrpcService<GreeterService>();

await app.RunAsync();


