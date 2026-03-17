# @seliseblocks/lmt-client

TypeScript client for structured logging and distributed tracing with Azure Service Bus.

## Installation

```bash
npm install @seliseblocks/lmt-client @azure/service-bus @opentelemetry/api @opentelemetry/sdk-trace-node
```

## Setup

### 1. Logger

```typescript
import { createLmtLogger } from '@seliseblocks/lmt-client';

const logger = createLmtLogger({
  serviceName: 'my-service',
  serviceBusConnectionString: process.env.SERVICE_BUS_CONNECTION_STRING!,
  logBatchSize: 100,
  flushIntervalSeconds: 5
});

logger.logInformation('App started');
logger.logError('Error', new Error('Failed'), { userId: '123' });

// Cleanup
process.on('SIGTERM', async () => await logger.dispose());
```

### 2. Distributed Tracing

```typescript
import { NodeTracerProvider } from '@opentelemetry/sdk-trace-node';
import { createLmtSpanProcessor } from '@seliseblocks/lmt-client';

const provider = new NodeTracerProvider();
provider.addSpanProcessor(createLmtSpanProcessor({
  serviceName: 'my-service',
  serviceBusConnectionString: process.env.SERVICE_BUS_CONNECTION_STRING!
}));
provider.register();

// Cleanup
process.on('SIGTERM', async () => await provider.shutdown());
```

## Multi-Tenant Tracing

**Important**: Set `TenantId` as a span attribute to enable multi-tenant trace grouping.

### Extract from x-blocks-key Header (Express Example)

```typescript
import express from 'express';
import { trace, context } from '@opentelemetry/api';

const app = express();

// Middleware to extract TenantId from x-blocks-key header
app.use((req, res, next) => {
  const tenantId = req.headers['x-blocks-key'];
  if (tenantId) {
    const span = trace.getSpan(context.active());
    if (span) {
      span.setAttribute('TenantId', tenantId as string);
    }
  }
  next();
});
```

### Manual TenantId Setting

```typescript
import { trace } from '@opentelemetry/api';

const tracer = trace.getTracer('my-service');
const span = tracer.startSpan('operation');

// Set TenantId attribute
span.setAttribute('TenantId', 'tenant-123');

// Your code...
span.end();
```

## API

### Logger Methods

```typescript
logger.logTrace(message, properties?)
logger.logDebug(message, properties?)
logger.logInformation(message, properties?)
logger.logWarning(message, properties?)
logger.logError(message, exception?, properties?)
logger.logCritical(message, exception?, properties?)
```

### Configuration Options

```typescript
interface LmtOptions {
  serviceName: string;                    // Required
  serviceBusConnectionString: string;     // Required
  logBatchSize?: number;                  // Default: 100
  traceBatchSize?: number;                // Default: 1000
  flushIntervalSeconds?: number;          // Default: 5
  maxRetries?: number;                    // Default: 3
  maxFailedBatches?: number;              // Default: 100
  enableLogging?: boolean;                // Default: true
  enableTracing?: boolean;                // Default: true
}
```

## Complete Example

```typescript
import express from 'express';
import { NodeTracerProvider } from '@opentelemetry/sdk-trace-node';
import { trace, context } from '@opentelemetry/api';
import { createLmtLogger, createLmtSpanProcessor } from '@seliseblocks/lmt-client';

const app = express();

// Setup logger
const logger = createLmtLogger({
  serviceName: 'my-api',
  serviceBusConnectionString: process.env.SERVICE_BUS_CONNECTION_STRING!
});

// Setup tracing
const provider = new NodeTracerProvider();
provider.addSpanProcessor(createLmtSpanProcessor({
  serviceName: 'my-api',
  serviceBusConnectionString: process.env.SERVICE_BUS_CONNECTION_STRING!
}));
provider.register();

// Extract TenantId from x-blocks-key
app.use((req, res, next) => {
  const tenantId = req.headers['x-blocks-key'];
  if (tenantId) {
    const span = trace.getSpan(context.active());
    if (span) span.setAttribute('TenantId', tenantId as string);
  }
  next();
});

// Request logging
app.use((req, res, next) => {
  res.on('finish', () => {
    logger.logInformation('Request', {
      method: req.method,
      path: req.path,
      status: res.statusCode
    });
  });
  next();
});

app.get('/api/users', (req, res) => {
  logger.logInformation('Fetching users');
  res.json({ users: [] });
});

const server = app.listen(3000);

// Graceful shutdown
process.on('SIGTERM', async () => {
  server.close(async () => {
    await logger.dispose();
    await provider.shutdown();
    process.exit(0);
  });
});
```

## How It Works

**Batching**: Logs/traces collected in memory, sent when batch size reached or interval elapsed.

**Retry**: Failed sends retried with exponential backoff. Failed batches queued and retried every 30s.

**Multi-Tenant**: Traces grouped by `TenantId` attribute. Missing TenantId → "Miscellaneous" group.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Traces missing TenantId | Set `span.setAttribute('TenantId', value)` in your code |
| Logs not appearing | Wait for flush interval or check Service Bus connection |
| "Exporter shutdown" error | Call `provider.shutdown()` during app shutdown |

## Notes

- `x-blocks-key` → `TenantId` mapping is YOUR convention (not enforced by library)
- Always call `dispose()` and `shutdown()` on exit
- TenantId can be from headers, JWT, database, etc. - library just reads the attribute

## License

MIT