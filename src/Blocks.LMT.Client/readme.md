# Blocks LMT Client

A production-ready .NET client library for centralized logging, metrics, and distributed tracing with automatic batching, built-in resilience, and multi-transport support.

## Overview

The Blocks LMT (Logging, Metrics, Tracing) Client provides enterprise-grade observability for .NET applications with:

- **Automatic Batching**: Reduces network overhead by batching logs and traces
- **Multiple Transport Options**: Azure Service Bus and RabbitMQ support
- **Resilient Retry Logic**: Configurable exponential backoff with failed batch queuing
- **OpenTelemetry Integration**: Direct integration with OTEL for distributed tracing
- **Multi-Tenant Support**: Automatic tenant isolation through baggage context
- **Thread-Safe**: Concurrent collections for high-throughput environments
- **MongoDB Storage**: Direct persistence fallback for logs and traces
- **Zero External Dependencies**: Minimal dependencies, works standalone or with Serilog/NLog

## Table of Contents

- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Usage](#usage)
- [Transport Options](#transport-options)
- [Architecture](#architecture)
- [Troubleshooting](#troubleshooting)
- [Related Documentation](#related-documentation)

---

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package SeliseBlocks.LMT.Client
```

Or via Package Manager Console:

```powershell
Install-Package SeliseBlocks.LMT.Client
```

---

## Quick Start

### 1. Configure in `appsettings.Development.json`

```json
{
  "Lmt": {
    "ServiceId": "my-api-service",
    "ConnectionString": "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey={key}",
    "XBlocksKey": "your-tenant-key",
    "LogBatchSize": 100,
    "TraceBatchSize": 1000,
    "FlushIntervalSeconds": 5,
    "MaxRetries": 3,
    "MaxFailedBatches": 100,
    "EnableLogging": true,
    "EnableTracing": true
  }
}
```

### 2. Register in `Program.cs`

```csharp
// Bind LMT options
var lmtOptions = builder.Configuration.GetSection("Lmt").Get<LmtOptions>()
    ?? throw new InvalidOperationException("Missing Lmt configuration.");

// Register LMT services
builder.Services.AddLmtClient(builder.Configuration);

// Configure OpenTelemetry with LMT
builder.Services.AddSingleton(new ActivitySource("my-api-service"));
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerBuilder =>
    {
        tracerBuilder
            .AddSource("my-api-service")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddLmtTracing(lmtOptions);
    });
```

### 3. Use in Your Application

```csharp
public class PaymentService
{
    private readonly IBlocksLogger _logger;
    private readonly ActivitySource _activitySource;

    public PaymentService(IBlocksLogger logger, ActivitySource activitySource)
    {
        _logger = logger;
        _activitySource = activitySource;
    }

    public async Task<PaymentResult> ProcessPaymentAsync(Order order)
    {
        using var activity = _activitySource.StartActivity("ProcessPayment");
        
        try
        {
            _logger.LogInformation("Processing payment for order {OrderId}", order.Id);
            
            var result = await _paymentGateway.ChargeAsync(order.Amount);
            
            _logger.LogInformation("Payment processed successfully for order {OrderId}", order.Id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Payment failed for order {OrderId}", ex, order.Id);
            throw;
        }
    }
}
```

---

## Configuration

### LmtOptions Properties

| Property | Type | Default | Required | Description |
|----------|------|---------|----------|-------------|
| `ServiceId` | `string` | - | ✅ | Unique service identifier for log/trace grouping |
| `ConnectionString` | `string` | - | ✅ | Azure Service Bus or RabbitMQ connection string |
| `XBlocksKey` | `string` | - | ✅ | Selise Blocks tenant key for multi-tenancy |
| `LogBatchSize` | `int` | `100` | ❌ | Number of logs to batch before sending |
| `TraceBatchSize` | `int` | `1000` | ❌ | Number of traces to batch before sending |
| `FlushIntervalSeconds` | `int` | `5` | ❌ | Seconds between automatic batch flushes |
| `MaxRetries` | `int` | `3` | ❌ | Maximum retry attempts for failed batches |
| `MaxFailedBatches` | `int` | `100` | ❌ | Maximum failed batches to queue before dropping |
| `EnableLogging` | `bool` | `true` | ❌ | Enable/disable logging functionality |
| `EnableTracing` | `bool` | `true` | ❌ | Enable/disable tracing functionality |

### Configuration Modes

The configuration system supports multiple approaches:

**Option 1: From `appsettings.json`**
```csharp
builder.Services.AddLmtClient(builder.Configuration);
```

**Option 2: Programmatic Configuration**
```csharp
var lmtOptions = new LmtOptions
{
    ServiceId = "my-service",
    ConnectionString = "amqp://localhost:5672",
    XBlocksKey = "tenant-key",
    FlushIntervalSeconds = 10,
    MaxRetries = 5
};

builder.Services.AddLmtClient(options =>
{
    options.ServiceId = lmtOptions.ServiceId;
    options.ConnectionString = lmtOptions.ConnectionString;
    options.XBlocksKey = lmtOptions.XBlocksKey;
});
```

**Option 3: Environment Variables**
```bash
export Lmt__ServiceId="my-service"
export Lmt__ConnectionString="amqp://localhost:5672"
export Lmt__XBlocksKey="tenant-key"
```

---

## Usage

### IBlocksLogger Interface

```csharp
public interface IBlocksLogger
{
    void Log(LmtLogLevel level, string messageTemplate, Exception? exception = null, params object?[] args);
    void LogTrace(string messageTemplate, params object?[] args);
    void LogDebug(string messageTemplate, params object?[] args);
    void LogInformation(string messageTemplate, params object?[] args);
    void LogWarning(string messageTemplate, params object?[] args);
    void LogError(string messageTemplate, Exception? exception = null, params object?[] args);
    void LogCritical(string messageTemplate, Exception? exception = null, params object?[] args);
}
```

### Log Levels

```csharp
_logger.LogTrace("Entering method ProcessPayment");                    // Most detailed

_logger.LogDebug("Payment gateway response received");                 // Debug info

_logger.LogInformation("Payment processed successfully");             // General flow

_logger.LogWarning("Payment took longer than expected");              // Handled issues

_logger.LogError("Payment failed", ex);                              // Errors

_logger.LogCritical("Payment gateway is down", ex);                  // Critical failures
```

### Structured Logging with Properties

Logs support structured properties through message templates:

```csharp
_logger.LogInformation("User {UserId} performed action {ActionName} on {ItemId}", 
    userId, actionName, itemId);

// Properties extracted: UserId, ActionName, ItemId
// Queryable in MongoDB: db.logs.find({ Properties.UserId: "123" })
```

### OpenTelemetry Tracing

```csharp
public async Task<Result> LongRunningOperationAsync()
{
    using var activity = _activitySource.StartActivity("LongRunningOperation");
    
    activity?.SetTag("operation.duration", "30s");
    activity?.SetTag("user.id", userId);
    
    try
    {
        var result = await _repository.QueryAsync();
        activity?.SetStatus(ActivityStatusCode.Ok);
        return result;
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
    }
}
```

---

## Transport Options

### Azure Service Bus

Best for cloud-native deployments and high-volume scenarios.

```json
{
  "Lmt": {
    "ServiceId": "my-service",
    "ConnectionString": "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey={key}",
    "XBlocksKey": "tenant-key"
  }
}
```

**Features:**
- Built-in retry and circuit breaker
- Message deduplication
- Scheduled delivery support
- Topic subscriptions for fan-out scenarios

### RabbitMQ

Best for on-premises deployments and hybrid scenarios.

```json
{
  "Lmt": {
    "ServiceId": "my-service",
    "ConnectionString": "amqp://username:password@localhost:5672/vhost",
    "XBlocksKey": "tenant-key"
  }
}
```

**Features:**
- Lightweight and easy to operate
- Message acknowledgment guarantees
- Durable queues
- Consumer priority support

### Fallback: MongoDB Direct Write

If both transports are unavailable, logs and traces write directly to MongoDB:

```json
{
  "BlocksSecret": {
    "LogConnectionString": "mongodb://localhost:27017",
    "TraceConnectionString": "mongodb://localhost:27017"
  }
}
```

---

## Architecture

### Batching Strategy

- **Buffering**: Logs and traces are buffered in memory
- **Automatic Flush**: Sent when batch size reached or interval elapsed
- **Retry Queue**: Failed batches queued for later retry
- **Concurrency**: Thread-safe concurrent collections for multi-threaded apps

---

## Troubleshooting

### No Logs Appearing

1. **Verify Configuration**: Check `appsettings.json` has valid `Lmt` section
2. **Check Connection**: Ensure ServiceBus or RabbitMQ is accessible
3. **Enable Logging**: Verify `EnableLogging: true` in configuration
4. **Check MongoDB Fallback**: Logs may be written to MongoDB if transport fails

```bash
# Query MongoDB for recent logs
db.logs.find({ ServiceName: "my-service" })
    .sort({ Timestamp: -1 })
    .limit(10)
```

### High Memory Usage

- **Reduce Batch Size**: Lower `LogBatchSize` or `TraceBatchSize`
- **Increase Flush Interval**: Lower `FlushIntervalSeconds` to flush more frequently
- **Reduce Max Failed Batches**: Limit queue size with `MaxFailedBatches`

### Connection Failures

For Service Bus:
```csharp
// Verify connection string format
Endpoint=sb://NAMESPACE.servicebus.windows.net/;
SharedAccessKeyName=RootManageSharedAccessKey;
SharedAccessKey=KEY
```

For RabbitMQ:
```bash
# Test connectivity
amqp://username:password@host:5672/vhost
```

### Timeout Issues

Increase flush interval or batch size to avoid frequent network calls:

```json
{
  "Lmt": {
    "FlushIntervalSeconds": 10,
    "LogBatchSize": 500,
    "MaxRetries": 5
  }
}
```

---

## Related Documentation

- [Blocks Genesis Framework](../README.md) - Main framework integration
- [Configuration Guide](../CONFIGURATION.md) - Complete configuration reference
- [Security Guidelines](../../SECURITY.md) - Security best practices
- [Main README](../../README.md) - Repository overview

---

## License

See [LICENSE](../../LICENSE) for details.