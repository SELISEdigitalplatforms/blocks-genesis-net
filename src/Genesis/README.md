# SeliseBlocks.Genesis

The .NET service foundation for SELISE `<blocks />` microservices. One package wires up configuration and secrets, JWT authentication, multi-tenant request handling, MongoDB, Redis, Azure Service Bus and RabbitMQ messaging, gRPC, and observability (logs, metrics, traces).

Requires .NET 10 (`net10.0`). Full repository documentation: <https://github.com/SELISEdigitalplatforms/blocks-genesis-net>.

## Installation

```bash
dotnet add package SeliseBlocks.Genesis
```

## Quick start: API service

```csharp
using Blocks.Genesis;

const string serviceName = "MyBlocksApi";

// Configure secrets and logging first. Pass VaultType.Azure or VaultType.OnPrem,
// or let ResolveVaultType() read the BLOCKS_VAULT_TYPE environment variable.
await ApplicationConfigurations.ConfigureLogAndSecretsAsync(
    serviceName, ApplicationConfigurations.ResolveVaultType());

var builder = WebApplication.CreateBuilder(args);
ApplicationConfigurations.ConfigureApiEnv(builder, args);
ApplicationConfigurations.ConfigureKestrel(builder);

var services = builder.Services;
ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
{
    ServiceName = serviceName,
    AzureServiceBusConfiguration = new AzureServiceBusConfiguration
    {
        Queues = ["demo_queue"],
        Topics = ["demo_topic"],
    },
});
ApplicationConfigurations.ConfigureApi(services, serviceName);

var app = builder.Build();
ApplicationConfigurations.ConfigureMiddleware(app);

await app.RunAsync();
```

## Quick start: worker service

Register one `IConsumer<T>` per message type; messages are routed to consumers by payload type name.

```csharp
using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string serviceName = "MyBlocksWorker";

await ApplicationConfigurations.ConfigureLogAndSecretsAsync(
    serviceName, ApplicationConfigurations.ResolveVaultType());

var messageConfiguration = new MessageConfiguration
{
    ServiceName = serviceName,
    AzureServiceBusConfiguration = new AzureServiceBusConfiguration
    {
        Queues = ["demo_queue"],
        Topics = ["demo_topic"],
    },
};

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<IConsumer<DemoMessage>, DemoMessageConsumer>();
        ApplicationConfigurations.ConfigureWorker(services, messageConfiguration);
    })
    .Build();

await host.RunAsync();

public sealed record DemoMessage(string Text);

public sealed class DemoMessageConsumer : IConsumer<DemoMessage>
{
    public Task Consume(DemoMessage context)
    {
        Console.WriteLine($"Received: {context.Text}");
        return Task.CompletedTask;
    }
}
```

## Configuration

`ConfigureLogAndSecretsAsync` loads a `.env` file when one exists at or above the working directory, then resolves secrets from Azure Key Vault (`VaultType.Azure`) or environment variables (`VaultType.OnPrem`, prefixed with `BlocksSecret__`). `DatabaseConnectionString` and `CacheConnectionString` are required; startup fails with a descriptive error when they are missing.

### Log and trace forwarding (LMT)

By default, logs are written to the console and to MongoDB (`BlocksSecret__LogConnectionString`), and traces to MongoDB (`BlocksSecret__TraceConnectionString`). To forward logs and traces to the central LMT pipeline over a message bus instead, set:

```bash
# Environment variable (or .env entry)
ServiceBusConnectionString=<your-service-bus-connection-string>

# Optional retry tuning
MaxRetries=3
MaxFailedBatches=100
```

`MaxRetries` and `MaxFailedBatches` can also be set in `appsettings.json`:

```json
{
  "Lmt": {
    "MaxRetries": 3,
    "MaxFailedBatches": 100
  }
}
```

| Setting | Required | Source | Default |
|---------|----------|--------|---------|
| `ServiceBusConnectionString` | Optional | Environment variable | unset (logs and traces go to console and MongoDB) |
| `MaxRetries` | Optional | `appsettings.json` or environment | `3` |
| `MaxFailedBatches` | Optional | `appsettings.json` or environment | `100` |

**Important:** keep real connection strings out of source control. Add `.env` to your `.gitignore`:

```gitignore
.env
.env.local
.env.*.local
```

## Middleware pipeline (API)

`HSTS -> CORS -> Health endpoints (/ping, /health/live, /health/ready) -> Swagger (when configured) -> Routing -> TenantValidation -> GlobalExceptionHandler -> Authentication -> Authorization -> Antiforgery -> Controllers`

## Local development

Start local infrastructure (MongoDB, Redis, RabbitMQ) from the repository root:

```bash
docker-compose up -d
```

Use `.env.example` as the baseline for environment variables.

## Migration notes

- `ConsumerMessage.ScheduledEnqueueTimeUtc` is the corrected property name; `SccheduledEnqueueTimeUtc` remains as an obsolete alias.
- `ConfigureAzureServiceBus` replaces `ConfigerAzureServiceBus`; the old name remains as an obsolete shim and will be removed in the next major version.
- `SecretEndPointAttribute` replaces the misspelled `SecretEnpPointAttribute`, which has been removed.

## License

MIT. See the repository LICENSE file.
