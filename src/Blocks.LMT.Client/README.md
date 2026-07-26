# SeliseBlocks.LMT.Client

A .NET client for the SELISE `<blocks />` LMT (logs, metrics, traces) pipeline. It batches log entries and OpenTelemetry trace spans and ships them over Azure Service Bus or RabbitMQ, with configurable retries and a bounded failed-batch queue to limit data loss during transient outages.

Requires .NET 10 (`net10.0`). The transport is selected from the connection string: `amqp://` or `amqps://` URIs use RabbitMQ, anything else is treated as an Azure Service Bus connection string.

## Installation

```bash
dotnet add package SeliseBlocks.LMT.Client
```

## Quick start

### 1. Configure the `Lmt` section in `appsettings.json`

```json
{
  "Lmt": {
    "ServiceId": "your-service-id",
    "ConnectionString": "<service-bus-or-amqp-connection-string>",
    "XBlocksKey": "your-blocks-key",
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

Keep real connection strings out of source control; supply them through environment variables or a secret store in production.

### 2. Register the client

```csharp
using SeliseBlocks.LMT.Client;

// Bind from the "Lmt" configuration section
builder.Services.AddLmtClient(builder.Configuration);

// Or configure in code
builder.Services.AddLmtClient(options =>
{
    options.ServiceId = "your-service-id";
    options.ConnectionString = Environment.GetEnvironmentVariable("LMT_CONNECTION_STRING") ?? string.Empty;
});
```

### 3. Add tracing (optional)

```csharp
using OpenTelemetry.Trace;
using SeliseBlocks.LMT.Client;
using System.Diagnostics;

var lmtOptions = new LmtOptions
{
    ServiceId = "your-service-id",
    ConnectionString = Environment.GetEnvironmentVariable("LMT_CONNECTION_STRING") ?? string.Empty,
};

builder.Services.AddSingleton(new ActivitySource("your-service-id"));
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerBuilder =>
    {
        tracerBuilder
            .AddSource("your-service-id")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddLmtTracing(lmtOptions);
    });
```

### 4. Log

```csharp
using SeliseBlocks.LMT.Client;
using System.Diagnostics;

public sealed class PaymentService
{
    private readonly IBlocksLogger _logger;
    private readonly ActivitySource _activitySource;

    public PaymentService(IBlocksLogger logger, ActivitySource activitySource)
    {
        _logger = logger;
        _activitySource = activitySource;
    }

    public void Process()
    {
        using var activity = _activitySource.StartActivity("Process");
        _logger.LogInformation("Payment processed at {dateTime}", DateTimeOffset.UtcNow);
    }
}
```

## Configuration reference (`LmtOptions`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ServiceId` | `string` | required | Unique identifier for your service; used to name the destination topic or exchange |
| `ConnectionString` | `string` | required | Azure Service Bus connection string, or an `amqp(s)://` URI for RabbitMQ |
| `XBlocksKey` | `string` | empty | Blocks tenant key attached to each log entry |
| `LogBatchSize` | `int` | `100` | Log entries per batch before an immediate flush |
| `TraceBatchSize` | `int` | `1000` | Trace spans per batch before an immediate flush |
| `FlushIntervalSeconds` | `int` | `5` | Interval for time-based flushes |
| `MaxRetries` | `int` | `3` | Retry attempts per failed send, with exponential backoff |
| `MaxFailedBatches` | `int` | `100` | Maximum failed batches kept for re-delivery; when the queue is full, further failed batches are dropped and a warning is logged |
| `EnableLogging` | `bool` | `true` | Toggles log shipping |
| `EnableTracing` | `bool` | `true` | Toggles trace shipping |

## Public API

```csharp
namespace SeliseBlocks.LMT.Client;

public interface IBlocksLogger
{
    void Log(LmtLogLevel level, string message, Exception? exception = null, params object?[] args);
    void LogTrace(string message, params object?[] args);
    void LogDebug(string message, params object?[] args);
    void LogInformation(string message, params object?[] args);
    void LogWarning(string message, params object?[] args);
    void LogError(string messageTemplate, Exception? exception = null, params object?[] args);
    void LogCritical(string message, Exception? exception = null, params object?[] args);
}

public enum LmtLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}
```

Additional public entry points: `LmtServiceExtensions.AddLmtClient` (both overloads), `LmtServiceExtensions.AddLmtTracing`, `LmtMessageSenderFactory.Create`, `ILmtMessageSender`, `LmtServiceBusSender`, `LmtRabbitMqSender`, `LmtTransportHelper.IsRabbitMq`, `LmtConstants`, `LogData`, and `TraceData`.

## License

MIT. See the repository LICENSE file: <https://github.com/SELISEdigitalplatforms/blocks-genesis-net>.
