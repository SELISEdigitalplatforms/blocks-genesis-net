# Blocks Genesis for .NET

SELISE `<blocks />` Genesis is the .NET service foundation used by Blocks microservices. It packages application bootstrap, configuration and secrets loading, JWT authentication, multi-tenant request handling, MongoDB access, Redis caching, Azure Service Bus and RabbitMQ messaging, gRPC plumbing, and observability (logs, metrics, traces) behind a small set of entry points.

## Packages

| Package | Project | Description |
|---|---|---|
| `SeliseBlocks.Genesis` | `src/Genesis` | The core service foundation described in this document |
| `SeliseBlocks.LMT.Client` | `src/Blocks.LMT.Client` | Standalone logging and tracing client with Azure Service Bus and RabbitMQ transports, consumed by Genesis and usable on its own |

Package documentation lives next to each project: [src/Genesis/README.md](./src/Genesis/README.md) and the `SeliseBlocks.LMT.Client` README in `src/Blocks.LMT.Client`.

## Requirements

- .NET SDK 10.0 or later (both packages target `net10.0`)
- For local integration runs: MongoDB, Redis, and a message broker (Azure Service Bus or RabbitMQ). A `docker-compose.yml` at the repository root starts MongoDB, Redis, and RabbitMQ.

## Installation

```sh
dotnet add package SeliseBlocks.Genesis
```

To use the logging and tracing client without the full foundation:

```sh
dotnet add package SeliseBlocks.LMT.Client
```

## Quickstart: API service

`ApplicationConfigurations` is the single entry point. Configure secrets and logging first, then services, then the middleware pipeline.

```csharp
using Blocks.Genesis;

const string serviceName = "MyBlocksApi";

// Loads .env if present, resolves secrets from the configured vault,
// and initializes Serilog with console and MongoDB sinks.
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

`ConfigureApi` requires the service name and optionally takes an API route prefix (default `api`, pass `"off"` to disable) and a service access resource name. Controllers are routed as `/{prefix}/[controller]/[action]`.

## Quickstart: worker service

Consumers implement `IConsumer<T>` and are discovered from the service collection. Messages are routed to a consumer by the payload type name.

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
    RabbitMqConfiguration = new RabbitMqConfiguration
    {
        ConsumerSubscriptions =
        [
            ConsumerSubscription.BindToQueue("demo_queue"),
        ],
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

## Sending messages

Inject `IMessageClient` (registered by `ConfigureServices`) and wrap the payload in a `ConsumerMessage<T>`:

```csharp
using Blocks.Genesis;

public sealed class DemoPublisher
{
    private readonly IMessageClient _messageClient;

    public DemoPublisher(IMessageClient messageClient)
    {
        _messageClient = messageClient;
    }

    public Task PublishAsync(DemoMessage message) =>
        _messageClient.SendToConsumerAsync(new ConsumerMessage<DemoMessage>
        {
            ConsumerName = "demo_queue",
            Payload = message,
        });
}
```

## Public API surface

All types below live in the `Blocks.Genesis` namespace.

| Area | Types |
|---|---|
| Bootstrap | `ApplicationConfigurations` (`ConfigureLogAndSecretsAsync`, `ResolveVaultType`, `ConfigureKestrel`, `ConfigureApiEnv`, `ConfigureWorkerEnv`, `ConfigureServices`, `ConfigureApi`, `ConfigureWorker`, `ConfigureMiddleware`, `ConfigureMicroserviceMiddleware`, `ConfigureApiBranchMiddleware`) |
| Request context | `BlocksContext` (tenant, user, roles, permissions of the current request), `TokenHelper` |
| Authorization | `ProtectedEndPointAttribute` (resource-based access check), `SecretEndPointAttribute` (HMAC shared-secret check via the `Secret` header) |
| Messaging | `IMessageClient`, `IConsumer<T>`, `ConsumerMessage<T>`, `MessageConfiguration`, `AzureServiceBusConfiguration`, `RabbitMqConfiguration`, `ConsumerSubscription`, `RoutingTable` |
| Data | `IDbContextProvider` (MongoDB collections per tenant), `ICacheClient` (Redis strings, hashes, pub/sub) |
| Tenancy | `ITenants`, `Tenant`, `TenantValidationMiddleware` |
| HTTP and gRPC | `IHttpService` (typed HTTP calls with header propagation), `IGrpcClientFactory`, `GrpcClientInterceptor`, `GrpcServerInterceptor` |
| Utilities | `ICryptoService` (SHA-256 hash, HMAC-SHA256, constant-time comparison), `IVault`, `VaultType` |
| Middleware | `GlobalExceptionHandlerMiddleware`, `RequestMetricsMiddleware`, `TenantValidationMiddleware` |
| API docs | `BlocksApiDocExtensions.AddBlocksSwagger`, `BlocksSwaggerOptions` |
| Exceptions | `BlocksException`, `BlocksValidationException`, `BlocksAuthenticationException`, `BlocksNotFoundException`, `BlocksRateLimitException` |

## Configuration

Secrets are resolved by `ConfigureLogAndSecretsAsync` from Azure Key Vault (`VaultType.Azure`) or from environment variables (`VaultType.OnPrem`). `ResolveVaultType()` reads the `BLOCKS_VAULT_TYPE` environment variable (`Azure` or `OnPrem`) and falls back to the given default. A `.env` file at or above the working directory is loaded automatically.

### Core secrets (OnPrem: environment variables with the `BlocksSecret__` prefix)

- `BlocksSecret__DatabaseConnectionString` (required)
- `BlocksSecret__CacheConnectionString` (required)
- `BlocksSecret__MessageConnectionString`
- `BlocksSecret__LogConnectionString`
- `BlocksSecret__MetricConnectionString`
- `BlocksSecret__TraceConnectionString`
- `BlocksSecret__LogDatabaseName`
- `BlocksSecret__MetricDatabaseName`
- `BlocksSecret__TraceDatabaseName`
- `BlocksSecret__RootDatabaseName`
- `BlocksSecret__EnableHsts`
- `BlocksSecret__AllowedCorsOrigins` (comma-separated absolute origins for credentialed CORS)

With Azure Key Vault the same names are used without the `BlocksSecret__` prefix (for example `DatabaseConnectionString`).

### Runtime settings (environment variables)

| Variable | Default | Purpose |
|---|---|---|
| `BLOCKS_VAULT_TYPE` | none | Overrides the vault type passed to `ResolveVaultType` |
| `HTTP1_PORT` | `5000` | Kestrel HTTP/1.1 listener (REST) |
| `HTTP2_PORT` | `5001` | Kestrel HTTP/2 listener (gRPC) |
| `BLOCKS_RATE_LIMIT_PER_MINUTE` | `120` | Fixed-window request limit per tenant, or per client IP when no `tenant-id` header is present |
| `ServiceBusConnectionString` | none | When set, logs and traces are forwarded to the LMT pipeline over the message bus instead of being written directly to MongoDB |
| `MaxRetries` | `3` | LMT send retry attempts (also settable as `Lmt:MaxRetries` in `appsettings.json`) |
| `MaxFailedBatches` | `100` | LMT failed-batch queue size (also settable as `Lmt:MaxFailedBatches`) |

`src/Genesis/setup_env.sh` exports placeholder values for all core secrets for a local session (`source setup_env.sh`), and `.env.example` at the repository root shows a working local configuration for the docker-compose services.

## Built-in endpoints and pipeline

Every API service exposes health endpoints: `/ping` (all checks), `/health/live` (liveness), and `/health/ready` (MongoDB and Redis readiness).

Middleware order set up by `ConfigureMiddleware`:

`HSTS -> CORS -> Health endpoints -> Swagger (when configured) -> Routing -> TenantValidation -> GlobalExceptionHandler -> RateLimiter -> Authentication -> Authorization -> Antiforgery -> Controllers`

## Repository layout

```text
.
├── src
│   ├── Genesis                # SeliseBlocks.Genesis package source
│   ├── Blocks.LMT.Client      # SeliseBlocks.LMT.Client package source
│   ├── TestDriver             # Internal gRPC sample client, not published
│   ├── XUnitTest              # Unit test suite (xUnit + Moq)
│   └── blocks-genesis-net.sln
├── docker-compose.yml         # Local MongoDB, Redis, RabbitMQ
└── .env.example               # Sample local configuration
```

## Building and testing

From the repository root:

```sh
dotnet restore src/blocks-genesis-net.sln
dotnet build src/blocks-genesis-net.sln
dotnet test src/XUnitTest/XUnitTest.csproj
```

Start local infrastructure for integration scenarios:

```sh
docker-compose up -d
```

## Versioning and compatibility

- The package major version tracks the target framework: `SeliseBlocks.Genesis` 10.x targets `net10.0`.
- Every public type and member is a compatibility contract. This package is consumed by the full set of Blocks services, so any change to a public signature, default value, or behavior is a breaking change for all of them and is only made deliberately with a version bump.
- Superseded members are kept with `[Obsolete]` markers for at least one release before removal (see the migration notes in [src/Genesis/README.md](./src/Genesis/README.md)).

## Contributing and security

- Contribution workflow, branch model, and coding guidelines: [CONTRIBUTING.md](./CONTRIBUTING.md)
- Vulnerability reporting: [SECURITY.md](./SECURITY.md)
- Community standards: [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md)

## License

Distributed under the [MIT License](./LICENSE).
