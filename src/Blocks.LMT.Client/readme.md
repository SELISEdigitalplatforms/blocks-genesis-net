# SeliseBlocks.LMT.Client

A robust and high-performance .NET client library for logging and distributed tracing with Azure Service Bus integration. Designed for enterprise applications requiring centralized log and trace management with built-in resilience, batching, and automatic retry mechanisms.

## ✨ Features

- **🚀 High Performance** - Automatic batching reduces network overhead and improves throughput
- **🔄 Automatic Retry Logic** - Exponential backoff with configurable retry attempts
- **💾 Failed Batch Queue** - Prevents data loss during transient failures
- **🧵 Thread-Safe** - Built with concurrent collections for multi-threaded environments
- **📊 OpenTelemetry Integration** - Industry-standard distributed tracing support
- **🏢 Multi-Tenant Support** - Automatic tenant isolation via baggage propagation
- **⚡ Zero Dependencies on Logging Frameworks** - Works independently or alongside Serilog, NLog, etc.
- **🎯 Azure Service Bus Native** - Optimized for Azure Service Bus Topics and Subscriptions
- **🔌 Easy Integration** - Simple dependency injection setup with minimal configuration

## 🏗️ Architecture

```
┌────────────────────────────────────────────────────────────────┐
│                      Your Application                          │
│  ┌──────────────┐              ┌──────────────────────┐        │
│  │ IBlocksLogger│              │  OpenTelemetry       │        │
│  │   (Logs)     │              │  (Traces)            │        │
│  └──────┬───────┘              └──────────┬───────────┘        │
│         │                                 │                    │
│         │  Batching & Retry               │  Batching & Retry  │
│         ▼                                 ▼                    │
│  ┌──────────────────────────────────────────────────────┐      │
│  │              Azure Service Bus                       │      │
│  └──────────────────┬───────────────────────────────────┘      │
└─────────────────────┼──────────────────────────────────────────┘
                      │
                      ▼
          ┌───────────────────────┐
          │  LMT Service Worker   │
          │  (Subscriptions)      │
          └───────────┬───────────┘
                      │
                      ▼
          ┌───────────────────────┐
          │   MongoDB Storage     │
          │  • Logs by Service    │
          │  • Traces by Tenant   │
          └───────────────────────┘
```

## 📦 Installation

Install via NuGet Package Manager:

```bash
dotnet add package SeliseBlocks.LMT.Client
```

Or via Package Manager Console:

```powershell
Install-Package SeliseBlocks.LMT.Client
```

## 🚀 Quick Start

### 1. Add to your `appsettings.Development.json`:

```json
{
  "Lmt": {
    "ServiceId": "your-service-id",
    "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key",
    "XBlocksKey": "your-XBlocksKey",
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

### 2. Register services in `Program.cs` or `Startup.cs`:

```csharp

// Add LMT Client
builder.Services.AddLmtClient(builder.Configuration);

// Add OpenTelemetry for distributed tracing
builder.Services.AddSingleton(new ActivitySource("your-serviceId"));
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerBuilder =>
    {
        tracerBuilder
            .AddSource("your-serviceId") // Match your ServiceId
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddLmtTracing(builder.Services.BuildServiceProvider()
                .GetRequiredService<LmtOptions>());
    });
```

### 3. Demo code:

```csharp

    public class Test
    {
        private readonly IBlocksLogger _logger;
        private readonly ActivitySource _activitySource;

        public Test(IBlocksLogger logger, ActivitySource activitySource)
        {
            _logger = logger;
            _activitySource = activitySource;
        }

        public string LogTest()
        {
            using var activity = _activitySource.StartActivity("LogTest");
            _logger.LogInformation("LogTest method call");
            return "Test successful";
        }
    }
```

## ⚙️ Configuration

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ServiceId` | `string` | *required* | Unique identifier for your service |
| `ServiceBusConnectionString` | `string` | *required* | Azure Service Bus connection string |
| `XBlocksKey` | `string` | *required* | | Selise blocks cloud key |
| `LogBatchSize` | `int` | `100` | Number of logs to batch before sending |
| `TraceBatchSize` | `int` | `1000` | Number of traces to batch before sending |
| `FlushIntervalSeconds` | `int` | `5` | Interval to flush batches automatically |
| `MaxRetries` | `int` | `3` | Maximum retry attempts for failed sends |
| `MaxFailedBatches` | `int` | `100` | Maximum failed batches to queue |
| `EnableLogging` | `bool` | `true` | Enable/disable logging |
| `EnableTracing` | `bool` | `true` | Enable/disable tracing |


#### Log Levels

```csharp
// Trace - Most detailed information
_logger.LogTrace("Entering method ProcessPayment");

// Debug - Debugging information
_logger.LogDebug("Payment gateway response received");

// Information - General flow
_logger.LogInformation("Payment processed successfully {dateTime}",  DateTimeOffset.UtcNow);

// Warning - Unexpected but handled situations
_logger.LogWarning("Payment took longer than expected");

// Error - Errors and exceptions
_logger.LogError("Payment failed", exception);

// Critical - Critical failures
_logger.LogCritical("Payment gateway is down", exception);
```


### IBlocksLogger Interface

```csharp
 public interface IBlocksLogger
    {
        void Log(LmtLogLevel level, string message, Exception exception = null, params object?[] args);
        void LogTrace(string message, params object?[] args );
        void LogDebug(string message, params object?[] args);
        void LogInformation(string message, params object?[] args);
        void LogWarning(string message, params object?[] args);
        void LogError(string messageTemplate, Exception? exception = null, params object?[] args);
        void LogCritical(string message, Exception exception = null, params object?[] args);
    }
```

### LmtLogLevel Enum

```csharp
public enum LmtLogLevel
{
    Trace = 0,      // Most detailed
    Debug = 1,      // Debug information
    Information = 2, // General flow
    Warning = 3,    // Unexpected situations
    Error = 4,      // Errors and exceptions
    Critical = 5    // Critical failures
}
```


## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.