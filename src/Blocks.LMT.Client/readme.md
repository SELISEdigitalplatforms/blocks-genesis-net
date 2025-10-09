# Blocks.LMT.Client

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
│  │  ILmtLogger  │              │  OpenTelemetry       │        │
│  │   (Logs)     │              │  (Traces)            │        │
│  └──────┬───────┘              └──────────┬───────────┘        │
│         │                                  │                   │
│         │  Batching & Retry                │  Batching & Retry │
│         ▼                                  ▼                   │
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
dotnet add package Blocks.LMT.Client
```

Or via Package Manager Console:

```powershell
Install-Package Blocks.LMT.Client
```

## 🚀 Quick Start

### 1. Add to your `appsettings.json`:

```json
{
  "Lmt": {
    "ServiceName": "MyMicroservice",
    "LogsServiceBusConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key",
    "TracesServiceBusConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key",
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
using Blocks.LMT.Client;

// Add LMT Client
builder.Services.AddLmtClient(builder.Configuration);

// Add OpenTelemetry for distributed tracing
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerBuilder =>
    {
        tracerBuilder
            .AddSource("MyMicroservice") // Match your ServiceName
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddLmtTracing(builder.Services.BuildServiceProvider()
                .GetRequiredService<LmtOptions>());
    });
```

### 3. Use in your code:

```csharp
public class OrderService
{
    private readonly ILmtLogger _logger;
    private readonly ActivitySource _activitySource;

    public OrderService(ILmtLogger logger)
    {
        _logger = logger;
        _activitySource = new ActivitySource("MyMicroservice");
    }

    public async Task<Order> CreateOrder(CreateOrderRequest request)
    {
        using var activity = _activitySource.StartActivity("CreateOrder");
        
        _logger.LogInformation("Creating order", new Dictionary<string, object>
        {
            { "CustomerId", request.CustomerId },
            { "OrderTotal", request.Total }
        });

        try
        {
            // Your business logic
            var order = await ProcessOrder(request);
            
            _logger.LogInformation("Order created successfully", new Dictionary<string, object>
            {
                { "OrderId", order.Id },
                { "CustomerId", request.CustomerId }
            });
            
            return order;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create order", ex, new Dictionary<string, object>
            {
                { "CustomerId", request.CustomerId },
                { "ErrorType", ex.GetType().Name }
            });
            throw;
        }
    }
}
```

## ⚙️ Configuration

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ServiceName` | `string` | *required* | Unique identifier for your service |
| `LogsServiceBusConnectionString` | `string` | *required* | Azure Service Bus connection string |
| `TracesServiceBusConnectionString` | `string` | *required* | Azure Service Bus connection string |
| `LogBatchSize` | `int` | `100` | Number of logs to batch before sending |
| `TraceBatchSize` | `int` | `1000` | Number of traces to batch before sending |
| `FlushIntervalSeconds` | `int` | `5` | Interval to flush batches automatically |
| `MaxRetries` | `int` | `3` | Maximum retry attempts for failed sends |
| `MaxFailedBatches` | `int` | `100` | Maximum failed batches to queue |
| `EnableLogging` | `bool` | `true` | Enable/disable logging |
| `EnableTracing` | `bool` | `true` | Enable/disable tracing |

### Configuration via Code

Instead of using `appsettings.json`, you can configure via code:

```csharp
services.AddLmtClient(options =>
{
    options.ServiceName = "MyMicroservice";
    options.LogsServiceBusConnectionString = "Endpoint=sb://...";
    options.TracesServiceBusConnectionString = "Endpoint=sb://...";
    options.LogBatchSize = 100;
    options.TraceBatchSize = 1000;
    options.FlushIntervalSeconds = 5;
    options.EnableLogging = true;
    options.EnableTracing = true;
});
```

### Environment Variables

You can also use environment variables (useful for containerized environments):

```bash
Lmt__ServiceName=MyMicroservice
Lmt__LogsServiceBusConnectionString=Endpoint=sb://...
Lmt__TracesServiceBusConnectionString=Endpoint=sb://...
Lmt__LogBatchSize=100
Lmt__EnableLogging=true
```

## 📚 Usage

### Logging

#### Basic Logging

```csharp
public class UserService
{
    private readonly ILmtLogger _logger;

    public UserService(ILmtLogger logger)
    {
        _logger = logger;
    }

    public async Task RegisterUser(User user)
    {
        _logger.LogInformation($"Registering user: {user.Email}");
        
        // Your logic
        
        _logger.LogInformation("User registered successfully", new Dictionary<string, object>
        {
            { "UserId", user.Id },
            { "Email", user.Email }
        });
    }
}
```

#### Log Levels

```csharp
// Trace - Most detailed information
_logger.LogTrace("Entering method ProcessPayment");

// Debug - Debugging information
_logger.LogDebug("Payment gateway response received");

// Information - General flow
_logger.LogInformation("Payment processed successfully");

// Warning - Unexpected but handled situations
_logger.LogWarning("Payment took longer than expected");

// Error - Errors and exceptions
_logger.LogError("Payment failed", exception);

// Critical - Critical failures
_logger.LogCritical("Payment gateway is down", exception);
```

#### Structured Logging

```csharp
_logger.LogInformation("Order processed", new Dictionary<string, object>
{
    { "OrderId", orderId },
    { "CustomerId", customerId },
    { "Total", totalAmount },
    { "ItemCount", items.Count },
    { "ProcessingTime", processingTime.TotalMilliseconds }
});
```

#### Exception Logging

```csharp
try
{
    await ProcessPayment(payment);
}
catch (PaymentException ex)
{
    _logger.LogError("Payment processing failed", ex, new Dictionary<string, object>
    {
        { "PaymentId", payment.Id },
        { "Amount", payment.Amount },
        { "Currency", payment.Currency },
        { "Gateway", payment.Gateway }
    });
    throw;
}
```


## 🎯 Advanced Scenarios

### Custom Batch Sizes for High-Throughput Services

```csharp
services.AddLmtClient(options =>
{
    options.ServiceName = "HighThroughputService";
    options.ServiceBusConnectionString = "...";
    options.LogBatchSize = 500;      // Larger batches
    options.TraceBatchSize = 5000;    // Much larger for traces
    options.FlushIntervalSeconds = 2; // More frequent flushes
});
```

### Conditional Logging/Tracing

```csharp
services.AddLmtClient(options =>
{
    options.ServiceName = "MyService";
    options.ServiceBusConnectionString = "...";
    
    // Disable in development, enable in production
    var environment = builder.Environment;
    options.EnableLogging = !environment.IsDevelopment();
    options.EnableTracing = !environment.IsDevelopment();
});
```

### Integration with Existing Logging Frameworks

You can use LMT alongside Serilog, NLog, or Microsoft.Extensions.Logging:

```csharp
public class PaymentService
{
    private readonly ILogger<PaymentService> _msLogger;  // Microsoft logger
    private readonly ILmtLogger _lmtLogger;              // LMT logger

    public PaymentService(
        ILogger<PaymentService> msLogger, 
        ILmtLogger lmtLogger)
    {
        _msLogger = msLogger;
        _lmtLogger = lmtLogger;
    }

    public async Task ProcessPayment(Payment payment)
    {
        // Log to console/file via MS Logger
        _msLogger.LogInformation("Processing payment {PaymentId}", payment.Id);
        
        // Send to centralized LMT system
        _lmtLogger.LogInformation("Processing payment", new Dictionary<string, object>
        {
            { "PaymentId", payment.Id },
            { "Amount", payment.Amount }
        });
        
        // Your logic
    }
}
```

### Sampling for High-Volume Traces

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerBuilder =>
    {
        tracerBuilder
            .AddSource("MyMicroservice")
            // Sample only 10% of traces to reduce volume
            .SetSampler(new TraceIdRatioBasedSampler(0.1))
            .AddLmtTracing(builder.Services.BuildServiceProvider()
                .GetRequiredService<LmtOptions>());
    });
```

## 📖 API Reference

### ILmtLogger Interface

```csharp
public interface ILmtLogger
{
    void Log(LmtLogLevel level, string message, Exception exception = null, 
             Dictionary<string, object> properties = null);
    void LogTrace(string message, Dictionary<string, object> properties = null);
    void LogDebug(string message, Dictionary<string, object> properties = null);
    void LogInformation(string message, Dictionary<string, object> properties = null);
    void LogWarning(string message, Dictionary<string, object> properties = null);
    void LogError(string message, Exception exception = null, 
                  Dictionary<string, object> properties = null);
    void LogCritical(string message, Exception exception = null, 
                     Dictionary<string, object> properties = null);
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

### Extension Methods

```csharp
// Register LMT Client
IServiceCollection.AddLmtClient(IConfiguration configuration)
IServiceCollection.AddLmtClient(Action<LmtOptions> configureOptions)

// Add LMT tracing to OpenTelemetry
TracerProviderBuilder.AddLmtTracing(LmtOptions options)
```

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.