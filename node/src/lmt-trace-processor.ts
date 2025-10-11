import {
  SpanExporter,
  ReadableSpan
} from '@opentelemetry/sdk-trace-base';
import { LmtOptions, TraceData } from './types';
import { LmtServiceBusSender } from './lmt-servicebus-sender';

export class LmtTraceExporter implements SpanExporter {
  private options: LmtOptions;
  private serviceBusSender: LmtServiceBusSender;
  private isShutdown = false;

  constructor(options: LmtOptions) {
    if (!options) {
      throw new Error('Options cannot be null');
    }

    if (!options.serviceName || options.serviceName.trim() === '') {
      throw new Error('ServiceName is required');
    }

    if (!options.serviceBusConnectionString || options.serviceBusConnectionString.trim() === '') {
      throw new Error('ServiceBusConnectionString is required');
    }

    this.options = {
      maxRetries: 3,
      maxFailedBatches: 100,
      enableTracing: true,
      ...options
    };

    this.serviceBusSender = new LmtServiceBusSender(
      this.options.serviceName,
      this.options.serviceBusConnectionString,
      this.options.maxRetries,
      this.options.maxFailedBatches
    );
  }

  async export(
    spans: ReadableSpan[],
    resultCallback: (result: { code: number; error?: Error }) => void
  ): Promise<void> {
    if (this.isShutdown) {
      resultCallback({
        code: 1,
        error: new Error('Exporter has been shutdown')
      });
      return;
    }

    if (!this.options.enableTracing) {
      resultCallback({ code: 0 });
      return;
    }

    try {
      const traces = this.convertSpansToTraceData(spans);

      if (traces.length > 0) {
        const tenantBatches = this.groupByTenant(traces);
        const pascalCaseBatches = this.convertToPascalCase(tenantBatches);
        await this.serviceBusSender.sendTraces(pascalCaseBatches);
      }

      resultCallback({ code: 0 });
    } catch (error) {
      console.error('Failed to export traces:', error);
      resultCallback({
        code: 1,
        error: error instanceof Error ? error : new Error(String(error))
      });
    }
  }

  async shutdown(): Promise<void> {
    if (this.isShutdown) {
      return;
    }

    this.isShutdown = true;

    try {
      await this.serviceBusSender.dispose();
    } catch (error) {
      console.error('Error during exporter shutdown:', error);
    }
  }

  async forceFlush(): Promise<void> {
    return Promise.resolve();
  }

  private convertSpansToTraceData(spans: ReadableSpan[]): TraceData[] {
    return spans.map(span => {
      const startTime = this.hrTimeToDate(span.startTime);
      const endTime = this.hrTimeToDate(span.endTime);
      const duration = endTime.getTime() - startTime.getTime();

      // FIX: Extract baggage from span attributes, not active context
      const baggage = this.extractBaggageFromSpan(span);
      const tenantId = baggage['TenantId'] || baggage['tenantId'] || 'Miscellaneous';

      return {
        timestamp: endTime,
        traceId: span.spanContext().traceId,
        spanId: span.spanContext().spanId,
        parentSpanId: span.parentSpanId || '',
        parentId: '',
        kind: this.getSpanKind(span.kind),
        activitySourceName: span.instrumentationLibrary.name,
        operationName: span.name,
        startTime,
        endTime,
        duration,
        attributes: this.convertAttributes(span.attributes),
        status: this.getStatus(span.status.code),
        statusDescription: span.status.message || '',
        baggage,
        serviceName: this.options.serviceName,
        tenantId
      };
    });
  }

  // FIX: Extract baggage from span attributes where we stored it
  private extractBaggageFromSpan(span: ReadableSpan): Record<string, string> {
    const baggage: Record<string, string> = {};
    
    // Extract from span attributes (where we should store baggage values)
    if (span.attributes) {
      // Look for baggage attributes (prefixed with 'baggage.')
      for (const [key, value] of Object.entries(span.attributes)) {
        if (key.startsWith('baggage.')) {
          const baggageKey = key.replace('baggage.', '');
          baggage[baggageKey] = String(value);
        }
        // Also check for TenantId directly
        if (key === 'TenantId' || key === 'tenantId') {
          baggage['TenantId'] = String(value);
        }
      }
    }

    return baggage;
  }

  private convertToPascalCase(tenantBatches: Record<string, TraceData[]>): Record<string, any[]> {
    const result: Record<string, any[]> = {};

    for (const [tenantId, traces] of Object.entries(tenantBatches)) {
      result[tenantId] = traces.map(trace => ({
        Timestamp: trace.timestamp,
        TraceId: trace.traceId,
        SpanId: trace.spanId,
        ParentSpanId: trace.parentSpanId,
        ParentId: trace.parentId,
        Kind: trace.kind,
        ActivitySourceName: trace.activitySourceName,
        OperationName: trace.operationName,
        StartTime: trace.startTime,
        EndTime: trace.endTime,
        Duration: trace.duration,
        Attributes: trace.attributes,
        Status: trace.status,
        StatusDescription: trace.statusDescription,
        Baggage: trace.baggage,
        ServiceName: trace.serviceName,
        TenantId: trace.tenantId
      }));
    }

    return result;
  }

  private hrTimeToDate(hrTime: [number, number]): Date {
    const milliseconds = hrTime[0] * 1000 + hrTime[1] / 1000000;
    return new Date(milliseconds);
  }

  private getSpanKind(kind: number): string {
    const kinds = ['INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER'];
    return kinds[kind] || 'INTERNAL';
  }

  private getStatus(code: number): string {
    switch (code) {
      case 0: return 'Unset';
      case 1: return 'Ok';
      case 2: return 'Error';
      default: return 'Unset';
    }
  }

  private convertAttributes(attributes: any): Record<string, any> {
    const result: Record<string, any> = {};
    
    if (attributes) {
      for (const [key, value] of Object.entries(attributes)) {
        result[key] = value;
      }
    }
    
    return result;
  }

  private groupByTenant(traces: TraceData[]): Record<string, TraceData[]> {
    const tenantBatches: Record<string, TraceData[]> = {};

    for (const trace of traces) {
      if (!tenantBatches[trace.tenantId]) {
        tenantBatches[trace.tenantId] = [];
      }
      tenantBatches[trace.tenantId].push(trace);
    }

    return tenantBatches;
  }
}