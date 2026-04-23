import { SpanExporter, ReadableSpan } from '@opentelemetry/sdk-trace-base';
import { LmtOptions } from './types';
export declare class LmtTraceExporter implements SpanExporter {
    private options;
    private serviceBusSender;
    private isShutdown;
    constructor(options: LmtOptions);
    export(spans: ReadableSpan[], resultCallback: (result: {
        code: number;
        error?: Error;
    }) => void): Promise<void>;
    shutdown(): Promise<void>;
    forceFlush(): Promise<void>;
    private convertSpansToTraceData;
    private extractBaggageFromSpan;
    private convertToPascalCase;
    private hrTimeToDate;
    private getSpanKind;
    private getStatus;
    private convertAttributes;
    private groupByTenant;
}
//# sourceMappingURL=lmt-trace-processor.d.ts.map