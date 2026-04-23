"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.LmtTraceExporter = void 0;
const lmt_servicebus_sender_1 = require("./lmt-servicebus-sender");
class LmtTraceExporter {
    constructor(options) {
        this.isShutdown = false;
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
        this.serviceBusSender = new lmt_servicebus_sender_1.LmtServiceBusSender(this.options.serviceName, this.options.serviceBusConnectionString, this.options.maxRetries, this.options.maxFailedBatches);
    }
    async export(spans, resultCallback) {
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
        }
        catch (error) {
            console.error('Failed to export traces:', error);
            resultCallback({
                code: 1,
                error: error instanceof Error ? error : new Error(String(error))
            });
        }
    }
    async shutdown() {
        if (this.isShutdown) {
            return;
        }
        this.isShutdown = true;
        try {
            await this.serviceBusSender.dispose();
        }
        catch (error) {
            console.error('Error during exporter shutdown:', error);
        }
    }
    async forceFlush() {
        return Promise.resolve();
    }
    convertSpansToTraceData(spans) {
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
    extractBaggageFromSpan(span) {
        const baggage = {};
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
    convertToPascalCase(tenantBatches) {
        const result = {};
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
    hrTimeToDate(hrTime) {
        const milliseconds = hrTime[0] * 1000 + hrTime[1] / 1000000;
        return new Date(milliseconds);
    }
    getSpanKind(kind) {
        const kinds = ['INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER'];
        return kinds[kind] || 'INTERNAL';
    }
    getStatus(code) {
        switch (code) {
            case 0: return 'Unset';
            case 1: return 'Ok';
            case 2: return 'Error';
            default: return 'Unset';
        }
    }
    convertAttributes(attributes) {
        const result = {};
        if (attributes) {
            for (const [key, value] of Object.entries(attributes)) {
                result[key] = value;
            }
        }
        return result;
    }
    groupByTenant(traces) {
        const tenantBatches = {};
        for (const trace of traces) {
            if (!tenantBatches[trace.tenantId]) {
                tenantBatches[trace.tenantId] = [];
            }
            tenantBatches[trace.tenantId].push(trace);
        }
        return tenantBatches;
    }
}
exports.LmtTraceExporter = LmtTraceExporter;
//# sourceMappingURL=lmt-trace-processor.js.map