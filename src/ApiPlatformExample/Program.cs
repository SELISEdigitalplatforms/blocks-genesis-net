using Blocks.Genesis;

const string serviceName = "Service-API-Platform-Example";

Action<GenesisSecretOptions> configurePlatformSecrets = options =>
{
    options.Mode = SecretMode.Platform;
    options.Platform = new PlatformOptions
    {
        BaseUrl = Environment.GetEnvironmentVariable("PLATFORM_SECRET_BASE_URL") ?? string.Empty,
        ClientId = Environment.GetEnvironmentVariable("PLATFORM_SECRET_CLIENT_ID") ?? string.Empty,
        XBlocksKey = Environment.GetEnvironmentVariable("PLATFORM_SECRET_XBLOCKS_KEY") ?? string.Empty
    };
};

await ApplicationConfigurations.ConfigureLogAndSecretsAsync(serviceName, SecretMode.Platform, configurePlatformSecrets);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddGenesisSecrets(configurePlatformSecrets);

var app = builder.Build();

app.MapGet("/", () => "Platform secret example is running");
app.MapControllers();

await app.RunAsync();
