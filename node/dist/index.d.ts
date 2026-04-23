export { LmtLogger } from './lmt-logger';
export { LmtServiceBusSender } from './lmt-servicebus-sender';
export { LmtTraceExporter } from './lmt-trace-processor';
export { LmtLogLevel, LmtOptions, LogData, TraceData, FailedLogBatch, FailedTraceBatch, ILmtLogger, LmtConstants } from './types';
import { LmtLogger } from './lmt-logger';
import { LmtTraceExporter } from './lmt-trace-processor';
import { LmtOptions } from './types';
import { BatchSpanProcessor } from '@opentelemetry/sdk-trace-base';
export declare function createLmtLogger(options: LmtOptions): LmtLogger;
export declare function createLmtTraceExporter(options: LmtOptions): LmtTraceExporter;
export declare function createLmtSpanProcessor(options: LmtOptions): BatchSpanProcessor;
//# sourceMappingURL=index.d.ts.map