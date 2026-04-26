using ApiOne.DomainService.Utilities;
using Blocks.Genesis;
using Microsoft.AspNetCore.DataProtection;
using TestDriver;

const string _serviceName = "Service-API-Test_One";

var builder = WebApplication.CreateBuilder(args);
ApplicationConfigurations.ConfigureApiEnv(builder, args);

var secret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName, VaultType.Azure);
var services = builder.Services;

ApplicationConfigurations.ConfigureServices(services, ApiConstant.GetMessageConfiguration(secret.MessageConnectionString));

ApplicationConfigurations.ConfigureApi(services);
services.AddSingleton<IGrpcClient, GrpcClient>();
var app = builder.Build();
ApplicationConfigurations.ConfigureMiddleware(app);

await app.RunAsync();
