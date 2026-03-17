export { LmtLogger } from './lmt-logger';
export { LmtServiceBusSender } from './lmt-servicebus-sender';
export { LmtTraceExporter } from './lmt-trace-exporter';

export {
  LmtLogLevel,
  LmtOptions,
  LogData,
  TraceData,
  FailedLogBatch,
  FailedTraceBatch,
  ILmtLogger,
  LmtConstants
} from './types';

import { LmtLogger } from './lmt-logger';
import { LmtTraceExporter } from './lmt-trace-exporter';
import { LmtOptions } from './types';
import { BatchSpanProcessor } from '@opentelemetry/sdk-trace-base';

export function createLmtLogger(options: LmtOptions): LmtLogger {
  return new LmtLogger(options);
}

export function createLmtTraceExporter(options: LmtOptions): LmtTraceExporter {
  return new LmtTraceExporter(options);
}

// Helper to create properly configured span processor (recommended)
export function createLmtSpanProcessor(options: LmtOptions): BatchSpanProcessor {
  const exporter = new LmtTraceExporter(options);
  return new BatchSpanProcessor(exporter, {
    maxQueueSize: options.traceBatchSize || 1000,
    scheduledDelayMillis: (options.flushIntervalSeconds || 5) * 1000
  });
}