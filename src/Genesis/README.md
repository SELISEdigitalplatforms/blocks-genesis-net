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

## Notifications

```csharp
services.AddBlocksNotifications();   // after the genesis services (API or worker)

publisher.Publish(new NotificationRequest
{
    Name = "iam-user-updated",           // KV notification-config:<Name> in the tenant DB, else the root built-in
    NamedUserIds = [affectedUserId],
    OrganizationId = recordOrgId,        // used only when there is no user token
    Payload = new { Fields = changed },  // JSON, max 256 KB
});
```

- `Publish` returns at once and never throws. A background worker reads a 1000-item buffer (full: dropped and logged).
- The tenant configuration decides receivers (level, scope, filters), channels (inbox, live, push) and the rate limit.
- Live goes to `NotificationHub` groups over the Redis backplane; the hub itself is mapped by blocks-logic only.
- Push sends `PushRequested` to queue `blocks_notification_push_listener` (consumed by the logic worker).

## Actions

```csharp
// After a successful save. IActionPublisher is registered by ConfigureServices (API and worker).
await actionPublisher.PublishAsync("iam-user-created", new { userId, organizationId });
```

- Reads the tenant's `actions` list in `KeyValueStores` (no cache, 2 s limit). Nothing is sent when the action is missing or all its targets are off.
- Keeps only the top-level data fields named in the action's `Payload` (case-sensitive, System.Text.Json default names); no data gives `{}`.
- Sends one `BlocksAction` per target that is on: `blocks_logic_action_workflow_listener`, `blocks_logic_action_function_listener`, `blocks_os_action_webhook_listener`. Tenant, user and trace travel in the message properties.
- `organizationId` null takes the organization from `BlocksContext`; empty means none.
- Best-effort: never throws; an envelope over 64 KB is dropped and logged; the data is never logged.

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

`HSTS -> CORS -> Health endpoints (/ping, /health/live, /health/ready) -> Swagger (when configured) -> Routing -> TenantValidation -> GlobalExceptionHandler -> Authentication -> Authorization -> UserRateLimit -> OnboardingApiAccess -> Antiforgery -> Controllers`

## Root cross-tenant API access

`OnboardingApiAccess` applies only when a root-tenant token calls another tenant (its `tenant_id` is the root tenant and the request's tenant, from the `x-blocks-key` header or query, is a different one). Such a request may reach only the paths listed in the `AllowedApis` array of the root database's `IdentityConfigurations` document. A path matches when it equals an entry case-insensitively, ignoring leading and trailing `/`. Every other request passes through without reading that list.

- The list is read at most once per 5 minutes per service instance, including when it is missing or empty, so a change to `AllowedApis` applies within 5 minutes. Non-string and blank entries are ignored.
- A path not in the list gets `403` with `"Forbidden: Cross_Tenant_Access_Not_Allowed"`.
- If the read fails or takes more than 2 s, the last good list keeps being used and the read is retried after 30 s. If no list has loaded yet, the request gets **`503 Service Unavailable`** with `Retry-After: 30` and `"Service_Unavailable: Os_Allowed_Apis_Unavailable"` until a read succeeds; retries are again 30 s apart.

## Rate limiting

Authorized and protected endpoints are rate limited per service and per authenticated subject (`user_id`, or `client_id` for `client_credentials` tokens), with counts shared through Redis under `ratelimit:{ServiceName}:{TenantId}:{user|client}:{id}`. Public endpoints are never limited. **Services can now return `429 Too Many Requests`** (gRPC: `RESOURCE_EXHAUSTED`).

- Limit (requests per second), resolved once at startup: `UserRateLimitPerSecond` env var, then the `UserRateLimitPerSecond` vault secret (`BlocksSecret__UserRateLimitPerSecond` on-prem), then `100`.
- Going over the limit blocks the subject for 30 s: `429`, `Retry-After: 30`, Problem Details body `"title":"Rate Limit Exceeded"`. Clients should wait for `Retry-After` before retrying.
- Allowed responses carry `RateLimit-Policy: "user";q=<limit>;w=1` and `RateLimit: "user";r=<remaining>;t=<reset>`.
- If Redis is slow (over 50 ms) or down, requests are allowed.
- `HttpService` no longer opens its circuit breaker on a downstream 429; it retries a 429 only when `Retry-After` fits in the request timeout.

See the repository README for the full contract.

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
- `WorkflowEntity.Settings` changes from `Dictionary<string, string>` to `WorkflowSettings?`, which holds the per-workflow failure `Alert`. This is a deliberate breaking change with no `[Obsolete]` alias; blocks-logic adopts it in blocks-logic#380. No data migration is needed: a stored `Settings` that is missing, `null`, `{}` or holds unknown keys still loads, and a default workflow now writes `Settings: { Alert: null }` instead of `Settings: {}`.
- The one incompatible stored shape is a `Settings.Alert` that is neither a document nor null, e.g. `{ "Alert": "text" }`; reading it throws `FormatException`.
- Rules for adding workflow settings: every new property is nullable and null means "use the default"; only add properties, never rename or retype one; every class has `[BsonIgnoreExtraElements]`; enums are stored as numbers.
- `OnboardingApiAccessMiddleware` now reads the root `AllowedApis` only for root cross-tenant requests, caches it for 5 minutes (an empty or missing list included), and answers those requests with `503` and `Retry-After: 30` instead of a `500` when the list cannot be loaded and none has loaded before. A list that was already loaded is now refreshed every 5 minutes instead of only on restart. Its public constructor now throws `ArgumentNullException` for a null `blocksSecret`, and it creates its root MongoDB client when the pipeline is built, so an invalid `DatabaseConnectionString` fails at startup instead of at the first request.
- 4.6.2 removes the Blocks Event Trigger forwarder (breaking): `IBlocksEventTriggerForwarder`, `BlocksEventTriggerMessage`, `BlocksEventCatalogEntry`, `BlocksEventTriggerConstants` and the message clients' forwarder constructors are gone. Message clients no longer forward sends; publish an action with `IActionPublisher` instead.

## License

MIT. See the repository LICENSE file.
