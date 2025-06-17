using Blocks.Genesis;
using GrpcServiceTestTemp.Services;

const string _serviceName = "Service-API-Test_Two";
var blocksSecret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName, VaultType.OnPrem);
var builder = WebApplication.CreateBuilder(args);
ApplicationConfigurations.ConfigureApiEnv(builder, args);
//ApplicationConfigurations.ConfigureKestrel(builder);

var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{

});

ApplicationConfigurations.ConfigureApi(services);
var app = builder.Build();
ApplicationConfigurations.ConfigureMiddleware(app);

app.MapGrpcService<GreeterService>();

await app.RunAsync();


