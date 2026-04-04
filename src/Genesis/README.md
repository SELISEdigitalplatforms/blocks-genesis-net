# Blocks Genesis Framework

A production-ready application framework for building scalable .NET services with built-in configuration management, middleware pipelines, multi-tenancy support, observability, and messaging integrations.

## Overview

Blocks Genesis provides a unified foundation for API and Worker services, eliminating boilerplate configuration and enabling teams to focus on business logic. The framework includes:

- **Immutable Configuration State**: Type-safe, DI-integrated bootstrap configuration  
- **Secure Secret Management**: Support for on-premises and Azure Key Vault configurations  
- **Multi-Tenant Context**: Request-scoped tenant isolation and enforcement  
- **Structured Observability**: Integrated logging, distributed tracing, and metrics via OpenTelemetry  
- **Message Broker Abstraction**: Pluggable Azure Service Bus and RabbitMQ support  
- **Fault Handling**: Global exception middleware with RFC7807-compliant error responses  
- **Cache Abstraction**: Redis-backed distributed caching layer  
- **gRPC & HTTP/REST**: Dual-protocol endpoint support with proper context propagation  

## Package

This package is **automatically included** in Blocks Genesis services. For standalone use:

```bash
dotnet add package SeliseBlocks.Genesis
```

## Quick Start

### API Service Example

```csharp
using Blocks.Genesis;

const string ServiceName = "my-api-service";

// 1. Configure secrets and logging
var secret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(
    serviceName: ServiceName,
    mode: SecretMode.OnPrem);  // or SecretMode.KeyVault

// 2. Create builder and configure environment
var builder = WebApplication.CreateBuilder(args);
ApplicationConfigurations.ConfigureApiEnv(builder, args);

// 3. Configure services
ApplicationConfigurations.ConfigureServices(
    builder.Services,
    messageConfiguration: new MessageConfiguration());

// 4. Build and configure middleware
var app = builder.Build();
ApplicationConfigurations.ConfigureMicroserviceMiddleware(app);

// 5. Start application
await app.RunAsync();
```

### Worker Service Example

```csharp
using Blocks.Genesis;

const string ServiceName = "my-worker-service";

// Initialize Genesis configuration
var secret = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(
    serviceName: ServiceName,
    mode: SecretMode.OnPrem);

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    var messageConfig = new MessageConfiguration
    {
        AzureServiceBusConfiguration = new AzureServiceBusConfiguration
        {
            Topics = new List<string> { "my-topic" }
        }
    };

    ApplicationConfigurations.ConfigureWorker(services, messageConfig);
    
    // Register worker consumers
    services.AddSingleton<IHostedService, MyWorkerService>();
});

await builder.Build().RunAsync();
```

## Configuration

Detailed configuration instructions are provided in [CONFIGURATION.md](./CONFIGURATION.md), which covers:

- **On-Premises Deployments**: Environment variable setup for self-hosted scenarios
- **Cloud Deployments**: Azure Key Vault integration
- **All Supported Platforms**: Windows, Linux, macOS, Docker, and Kubernetes
- **Troubleshooting**: Common issues and resolution steps

### Quick Reference

The framework uses the `BlocksSecret__` prefix for configuration variables. Key settings:

```env
# Infrastructure
BlocksSecret__CacheConnectionString=localhost:6379
BlocksSecret__DatabaseConnectionString=mongodb://localhost:27017
BlocksSecret__MessageConnectionString=amqp://localhost:5672

# Observability
BlocksSecret__LogConnectionString=mongodb://localhost:27017
BlocksSecret__TraceConnectionString=mongodb://localhost:27017

# Application
BlocksSecret__ServiceName=my-service
BlocksSecret__EnableHsts=false
```

See [CONFIGURATION.md](./CONFIGURATION.md) for complete variable reference and setup instructions.

## Key Features

### 1. Immutable Bootstrap State

Configuration is loaded once at startup and handed to Dependency Injection:

```csharp
var state = app.Services.GetRequiredService<ApplicationBootstrapState>();
var serviceName = state.ServiceName;
var secret = state.BlocksSecret;
```

### 2. Multi-Tenant Context

Secure request-scoped tenant access:

```csharp
public IActionResult GetData()
{
    var context = HttpContext.GetBlocksContext();
    var tenantId = context.TenantId;
    var userId = context.UserId;
    
    // Tenant-scoped database queries automatically applied
    return Ok(_service.GetData(tenantId));
}
```

### 3. Observability Pipeline

Automatic instrumentation with OpenTelemetry:

- Logs → MongoDB Collections
- Traces → MongoDB Collections  
- Metrics → MongoDB Collections
- Fallback → Service Bus / RabbitMQ

### 4. Message Broker Abstraction

Support for multiple transports with validation:

```csharp
var config = new MessageConfiguration
{
    AzureServiceBusConfiguration = new AzureServiceBusConfiguration
    {
        Topics = new List<string> { "my-topic" }
    }
    // Cannot also specify RabbitMqConfiguration (throws at startup)
};
```

### 5. Global Exception Handling

RFC7807-compliant error responses with trace context:

```json
{
    "title": "An unexpected error occurred.",
    "status": 500,
    "traceId": "0HN1GJ47H76UK:00000001"
}
```

## Project Structure

| Directory | Purpose |
|-----------|---------|
| `Auth/` | JWT, certificates, multi-tenant context |
| `Cache/` | Redis distributed caching |
| `Configuration/` | Bootstrap state, secrets, configuration |
| `Database/` | MongoDB abstractions |
| `Grpc/` | gRPC client factory |
| `Health/` | Health check endpoints |
| `Lmt/` | LMT exporters (logs, metrics, traces) |
| `Middlewares/` | Request/response pipeline |
| `Message/` | Message broker abstraction |
| `Tenant/` | Multi-tenant services |
| `Utilities/` | Constants and helpers |
| `Vault/` | Secret provider abstraction |

## Architecture Patterns

### Bootstrap State Management

Immutable record passed through DI:

```csharp
internal sealed record ApplicationBootstrapState(
    string ServiceName,
    IBlocksSecret BlocksSecret,
    BlocksSwaggerOptions? SwaggerOptions);
```

**Benefits**: No mutable statics, type safety, testability, clear DI contracts

### Shared Sender Pattern

Reference-counted message senders prevent connection pool exhaustion:

```csharp
var sender = LmtMessageSenderFactory.CreateShared(options);
// Shared across logger, trace processor, and exporters
// Disposed when final reference is released
```

### Structured Logging Fallbacks

Safe logging with DI fallback:

```csharp
private static ILogger GetAuthLogger(HttpContext httpContext)
{
    var loggerFactory = httpContext.RequestServices?.GetService<ILoggerFactory>();
    return loggerFactory?.CreateLogger("Blocks.Genesis.Auth") ?? NullLogger.Instance;
}
```

## Development & Testing

### Unit Testing

Use reflection helpers for internal-heavy components:

```csharp
var instance = (MyService)RuntimeHelpers.GetUninitializedObject(typeof(MyService));
SetField(instance, "_logger", mockLogger);
SetField(instance, "_cache", mockCache);
```

### Integration Testing

Spin up test containers for stateful services:

```csharp
var mongoContainer = new MongoDbBuilder().WithCleanUp(true).Build();
await mongoContainer.StartAsync();

Environment.SetEnvironmentVariable("BlocksSecret__DatabaseConnectionString",
    mongoContainer.GetConnectionString());
```

## Security

1. **Tokens Excluded from Serialization**: `[JsonIgnore]` prevents token leakage in context propagation
2. **Transport Validation**: Dual broker configuration is rejected at startup
3. **Tenant Enforcement**: All queries automatically scoped to authenticated tenant
4. **Error Standardization**: Structured error responses never leak internals
5. **Managed Secrets**: Azure Key Vault support for cloud deployments

## Troubleshooting

### Application Fails to Start

1. Verify required environment variables in [CONFIGURATION.md](./CONFIGURATION.md)
2. Check connection string formats (MongoDB, Redis, Service Bus, RabbitMQ)
3. Review logs for specific missing configuration keys

### Missing Observability Data

1. Verify MongoDB collections exist and have write access
2. Check LMT connection string configuration
3. Ensure message broker connectivity

### Multi-Tenant Context Issues

1. Confirm JWT token includes required tenant claims
2. Verify `BlocksContext` resolution from `HttpContext`
3. Check fallback chain for third-party token validation

## Related Documentation

- [Configuration Guide](./CONFIGURATION.md) - All deployment modes and environment setup
- [LMT Client](../Blocks.LMT.Client/readme.md) - Logging, metrics, tracing client library
- [Security Guide](../../SECURITY.md) - Security policies and best practices
- [Main README](../../README.md) - Repository overview

## License

See [LICENSE](../../LICENSE) for details.
