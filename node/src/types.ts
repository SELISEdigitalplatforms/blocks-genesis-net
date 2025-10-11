export enum LmtLogLevel {
  Trace = 0,
  Debug = 1,
  Information = 2,
  Warning = 3,
  Error = 4,
  Critical = 5
}

export interface LmtOptions {
  serviceName: string;
  serviceBusConnectionString: string;
  logBatchSize?: number;
  traceBatchSize?: number;
  flushIntervalSeconds?: number;
  maxRetries?: number;
  maxFailedBatches?: number;
  enableLogging?: boolean;
  enableTracing?: boolean;
}

export interface LogData {
  timestamp: Date;
  level: string;
  message: string;
  exception: string;
  serviceName: string;
  properties: Record<string, any>;
}

export interface TraceData {
  timestamp: Date;
  traceId: string;
  spanId: string;
  parentSpanId: string;
  parentId: string;
  kind: string;
  activitySourceName: string;
  operationName: string;
  startTime: Date;
  endTime: Date;
  duration: number;
  attributes: Record<string, any>;
  status: string;
  statusDescription: string;
  baggage: Record<string, string>;
  serviceName: string;
  tenantId: string;
}

export interface FailedLogBatch {
  logs: LogData[];
  retryCount: number;
  nextRetryTime: Date;
}

export interface FailedTraceBatch {
  tenantBatches: Record<string, TraceData[]>;
  retryCount: number;
  nextRetryTime: Date;
}

export interface ILmtLogger {
  log(
    level: LmtLogLevel,
    message: string,
    exception?: Error | null,
    properties?: Record<string, any>
  ): void;
  logTrace(message: string, properties?: Record<string, any>): void;
  logDebug(message: string, properties?: Record<string, any>): void;
  logInformation(message: string, properties?: Record<string, any>): void;
  logWarning(message: string, properties?: Record<string, any>): void;
  logError(
    message: string,
    exception?: Error | null,
    properties?: Record<string, any>
  ): void;
  logCritical(
    message: string,
    exception?: Error | null,
    properties?: Record<string, any>
  ): void;
  dispose(): Promise<void>;
}

export class LmtConstants {
  static readonly LOG_SUBSCRIPTION = 'blocks-lmt-service-logs';
  static readonly TRACE_SUBSCRIPTION = 'blocks-lmt-service-traces';

  static getTopicName(serviceName: string): string {
    return `lmt-${serviceName}`;
  }
}