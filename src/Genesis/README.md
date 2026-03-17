# SeliseBlocks.Genesis

## Installation

This package is **automatically included** in Blocks Genesis framework. No manual installation needed for Genesis-based services.

For standalone use:
```bash
dotnet add package SeliseBlocks.Genesis
```

## Quick Start for Genesis Services

### 1. API Service Example

```csharp
using Blocks.Genesis;
using TestDriver;

const string _serviceName = "Service-API-Test_One";

// Configure logs and secrets - LMT is automatically initialized here
await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName, VaultType.Azure); // VaultType.OnPrem

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
```

### 2. Worker Service Example

```csharp
using Blocks.Genesis;
using WorkerOne;

const string _serviceName = "Service-Worker-Test_One";

// Configure logs and secrets - LMT is automatically initialized here
var blocksSecrets = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName, VaultType.Azure); // VaultType.OnPrem

var messageConfiguration = new MessageConfiguration
{
   AzureServiceBusConfiguration = new()
   {
       Queues = new List<string> { "demo_queue" },
       Topics = new List<string> { "demo_topic", "demo_topic_1" }
   }
};

await CreateHostBuilder(args).Build().RunAsync();

IHostBuilder CreateHostBuilder(string[] args) =>
    Host.CreateDefaultBuilder(args).ConfigureServices((services) =>
    {
        services.AddHttpClient();
        services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
        services.AddSingleton<IConsumer<W2Context>, W2Consumer>();
        ApplicationConfigurations.ConfigureWorker(services, messageConfiguration);
    });
```

## Configuration

LMT Client is automatically configured when you call `ApplicationConfigurations.ConfigureLogAndSecretsAsync()`. 

### Option 1: Using `.env` File (Recommended for Local Development)

Create a `.env` file in your project root:

```bash
# LMT Service Bus Configuration
LogsServiceBusConnectionString=Endpoint=sb://your-logs-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key
TracesServiceBusConnectionString=Endpoint=sb://your-traces-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key

# Optional: Retry Configuration
MaxRetries=3
MaxFailedBatches=100

# Other service configuration
ASPNETCORE_ENVIRONMENT=Development
```

**Important:** Add `.env` to your `.gitignore`:
```gitignore
.env
.env.local
.env.*.local
```

### Option 2: Using `appsettings.json`

```json
{
  "Lmt": {
    "MaxRetries": 3,
    "MaxFailedBatches": 100
  }
}
```

**Note:** Service Bus connection strings must be set via environment variables (`.env` or system environment).

### Option 3: Using Environment Variables (For Docker/Production)

```bash
export LogsServiceBusConnectionString="Endpoint=sb://your-logs-namespace.servicebus.windows.net/;..."
export TracesServiceBusConnectionString="Endpoint=sb://your-traces-namespace.servicebus.windows.net/;..."
export MaxRetries=3
export MaxFailedBatches=100
```

### Configuration Priority

1. **`.env` file** (loaded first, sets environment variables)
2. **System environment variables** (can override .env)
3. **appsettings.json** `Lmt:*` section (for MaxRetries and MaxFailedBatches only)
4. **Default values** (MaxRetries=3, MaxFailedBatches=100)

### Required vs Optional

| Setting | Required | Source | Default |
|---------|----------|--------|---------|
| `LogsServiceBusConnectionString` | Required | Environment Variable | - |
| `TracesServiceBusConnectionString` | Required | Environment Variable | - |
| `MaxRetries` | Optional | appsettings.json or Environment | `3` |
| `MaxFailedBatches` | Optional | appsettings.json or Environment | `100` |

*If not configured, logs will only write to console and MongoDB (Service Bus integration is disabled).
