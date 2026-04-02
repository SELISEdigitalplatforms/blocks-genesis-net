using Blocks.Genesis;
using Serilog;

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

// Loads appsettings, env vars, and command-line args; also calls LmtConfigurationProvider.Initialize
// so Lmt:ConnectionString in appsettings / Lmt__ConnectionString env var is respected.
ApplicationConfigurations.ConfigureApiEnv(builder, args);

// Route ILogger<T> in controllers through Serilog → MongoDBDynamicSink → LMT transport
builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddGenesisSecrets(configurePlatformSecrets);

var app = builder.Build();

app.MapGet("/", () => "Platform secret example is running");
app.MapControllers();

await app.RunAsync();
