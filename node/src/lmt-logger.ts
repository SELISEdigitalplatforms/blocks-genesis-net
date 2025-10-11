import { trace, context } from '@opentelemetry/api';
import { LmtOptions, LogData, LmtLogLevel, ILmtLogger } from './types';
import { LmtServiceBusSender } from './lmt-servicebus-sender';

export class LmtLogger implements ILmtLogger {
  private options: LmtOptions;
  private logBatch: LogData[] = [];
  private flushTimer?: NodeJS.Timeout;
  private serviceBusSender: LmtServiceBusSender;
  private flushLock = false;
  private disposed = false;

  constructor(options: LmtOptions) {
    if (!options) {
      throw new Error('Options cannot be null');
    }

    if (!options.serviceName || options.serviceName.trim() === '') {
      throw new Error('ServiceName is required');
    }

    if (
      !options.serviceBusConnectionString ||
      options.serviceBusConnectionString.trim() === ''
    ) {
      throw new Error('ServiceBusConnectionString is required');
    }

    this.options = {
      logBatchSize: 100,
      traceBatchSize: 1000,
      flushIntervalSeconds: 5,
      maxRetries: 3,
      maxFailedBatches: 100,
      enableLogging: true,
      enableTracing: true,
      ...options
    };

    this.serviceBusSender = new LmtServiceBusSender(
      this.options.serviceName,
      this.options.serviceBusConnectionString,
      this.options.maxRetries,
      this.options.maxFailedBatches
    );

    const flushInterval = this.options.flushIntervalSeconds! * 1000;
    this.flushTimer = setInterval(() => {
      this.flushBatch().catch(console.error);
    }, flushInterval);
  }

  log(
    level: LmtLogLevel,
    message: string,
    exception?: Error | null,
    properties?: Record<string, any>
  ): void {
    if (!this.options.enableLogging) return;

    const span = trace.getSpan(context.active());
    const logData: LogData = {
      timestamp: new Date(),
      level: LmtLogLevel[level],
      message,
      exception: exception?.stack || exception?.message || '',
      serviceName: this.options.serviceName,
      properties: properties || {}
    };

    if (span) {
      const spanContext = span.spanContext();
      logData.properties['traceId'] = spanContext.traceId;
      logData.properties['spanId'] = spanContext.spanId;
    }

    this.logBatch.push(logData);

    if (this.logBatch.length >= this.options.logBatchSize!) {
      setImmediate(() => this.flushBatch().catch(console.error));
    }
  }

  logTrace(message: string, properties?: Record<string, any>): void {
    this.log(LmtLogLevel.Trace, message, null, properties);
  }

  logDebug(message: string, properties?: Record<string, any>): void {
    this.log(LmtLogLevel.Debug, message, null, properties);
  }

  logInformation(message: string, properties?: Record<string, any>): void {
    this.log(LmtLogLevel.Information, message, null, properties);
  }

  logWarning(message: string, properties?: Record<string, any>): void {
    this.log(LmtLogLevel.Warning, message, null, properties);
  }

  logError(
    message: string,
    exception?: Error | null,
    properties?: Record<string, any>
  ): void {
    this.log(LmtLogLevel.Error, message, exception, properties);
  }

  logCritical(
    message: string,
    exception?: Error | null,
    properties?: Record<string, any>
  ): void {
    this.log(LmtLogLevel.Critical, message, exception, properties);
  }

  private async flushBatch(): Promise<void> {
    if (this.flushLock) return;

    this.flushLock = true;
    try {
      const logs: LogData[] = [];
      while (this.logBatch.length > 0) {
        const log = this.logBatch.shift();
        if (log) logs.push(log);
      }

      if (logs.length > 0) {
        await this.serviceBusSender.sendLogs(logs);
      }
    } finally {
      this.flushLock = false;
    }
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;

    if (this.flushTimer) {
      clearInterval(this.flushTimer);
    }

    await this.flushBatch();
    await this.serviceBusSender.dispose();

    this.disposed = true;
  }
}