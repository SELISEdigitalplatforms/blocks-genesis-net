import {
  createLmtLogger,
  createLmtSpanProcessor,
  LmtOptions
} from '@seliseblocks/lmt-client';
import { NodeTracerProvider } from '@opentelemetry/sdk-trace-node';
import { trace, context } from '@opentelemetry/api';

const lmtOptions: LmtOptions = {
  serviceName: 'example-service',
  serviceBusConnectionString: process.env.SERVICE_BUS_CONNECTION_STRING || '',
  logBatchSize: 10,
  traceBatchSize: 50,
  flushIntervalSeconds: 3
};

// Setup
const logger = createLmtLogger(lmtOptions);
const provider = new NodeTracerProvider();
provider.addSpanProcessor(createLmtSpanProcessor(lmtOptions));
provider.register();

const tracer = trace.getTracer('example-service');

// Example 1: Basic Logging
function basicLogging() {
  console.log('\n=== Basic Logging ===');
  
  logger.logInformation('App started');
  logger.logDebug('Debug info', { mode: 'dev' });
  logger.logWarning('Warning message');
  
  try {
    throw new Error('Test error');
  } catch (error) {
    logger.logError('Error occurred', error as Error, { code: 'ERR001' });
  }
}

// Example 2: Basic Tracing
async function basicTracing() {
  console.log('\n=== Basic Tracing ===');
  
  const span = tracer.startSpan('parent-operation');
  
  await context.with(trace.setSpan(context.active(), span), async () => {
    span.setAttribute('user.id', '12345');
    logger.logInformation('Operation started');
    
    await sleep(100);
    
    const childSpan = tracer.startSpan('child-operation');
    await context.with(trace.setSpan(context.active(), childSpan), async () => {
      logger.logDebug('Child operation');
      await sleep(50);
      childSpan.end();
    });
    
    span.end();
  });
}

// Example 3: Multi-Tenant (Manual)
async function multiTenantTracing() {
  console.log('\n=== Multi-Tenant Tracing ===');
  
  const span = tracer.startSpan('tenant-operation');
  
  await context.with(trace.setSpan(context.active(), span), async () => {
    // Set TenantId attribute (would come from x-blocks-key in real app)
    span.setAttribute('TenantId', 'tenant-123');
    
    logger.logInformation('Processing tenant data', { tenantId: 'tenant-123' });
    await sleep(75);
    
    span.end();
  });
}

// Example 4: Simulating Express Request with x-blocks-key
async function simulateExpressRequest() {
  console.log('\n=== Simulated Express Request ===');
  
  // Simulate incoming request with x-blocks-key header
  const xBlocksKey = 'tenant-456';
  
  const span = tracer.startSpan('http-request');
  
  await context.with(trace.setSpan(context.active(), span), async () => {
    // Extract TenantId from x-blocks-key (middleware would do this)
    span.setAttribute('TenantId', xBlocksKey);
    span.setAttribute('http.method', 'GET');
    span.setAttribute('http.url', '/api/users');
    
    logger.logInformation('Request received', {
      method: 'GET',
      path: '/api/users',
      tenantId: xBlocksKey
    });
    
    await sleep(100);
    
    logger.logInformation('Request completed', { statusCode: 200 });
    span.end();
  });
}

// Example 5: Error Handling
async function errorHandling() {
  console.log('\n=== Error Handling ===');
  
  const span = tracer.startSpan('risky-operation');
  
  try {
    await context.with(trace.setSpan(context.active(), span), async () => {
      logger.logInformation('Starting risky operation');
      await sleep(50);
      throw new Error('Operation failed');
    });
  } catch (error) {
    logger.logError('Operation failed', error as Error, { severity: 'high' });
    span.recordException(error as Error);
    span.setStatus({ code: 2, message: (error as Error).message });
  } finally {
    span.end();
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

// Main
async function main() {
  console.log('Starting LMT Client Examples...\n');
  
  try {
    basicLogging();
    await basicTracing();
    await multiTenantTracing();
    await simulateExpressRequest();
    await errorHandling();
    
    console.log('\n✓ All examples completed. Waiting for flush...');
    await sleep(6000);
    
  } catch (error) {
    console.error('Example failed:', error);
  } finally {
    console.log('\nCleaning up...');
    await logger.dispose();
    await provider.shutdown();
    console.log('✓ Done!');
  }
}

main().catch(console.error);