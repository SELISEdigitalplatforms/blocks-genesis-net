"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.LmtLogger = void 0;
const api_1 = require("@opentelemetry/api");
const types_1 = require("./types");
const lmt_servicebus_sender_1 = require("./lmt-servicebus-sender");
class LmtLogger {
    constructor(options) {
        this.logBatch = [];
        this.flushLock = false;
        this.disposed = false;
        if (!options) {
            throw new Error('Options cannot be null');
        }
        if (!options.serviceName || options.serviceName.trim() === '') {
            throw new Error('ServiceName is required');
        }
        if (!options.serviceBusConnectionString ||
            options.serviceBusConnectionString.trim() === '') {
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
        this.serviceBusSender = new lmt_servicebus_sender_1.LmtServiceBusSender(this.options.serviceName, this.options.serviceBusConnectionString, this.options.maxRetries, this.options.maxFailedBatches);
        const flushInterval = this.options.flushIntervalSeconds * 1000;
        this.flushTimer = setInterval(() => {
            this.flushBatch().catch(console.error);
        }, flushInterval);
    }
    log(level, message, exception, properties) {
        if (!this.options.enableLogging)
            return;
        const span = api_1.trace.getSpan(api_1.context.active());
        const logData = {
            timestamp: new Date(),
            level: types_1.LmtLogLevel[level],
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
        if (this.logBatch.length >= this.options.logBatchSize) {
            setImmediate(() => this.flushBatch().catch(console.error));
        }
    }
    logTrace(message, properties) {
        this.log(types_1.LmtLogLevel.Trace, message, null, properties);
    }
    logDebug(message, properties) {
        this.log(types_1.LmtLogLevel.Debug, message, null, properties);
    }
    logInformation(message, properties) {
        this.log(types_1.LmtLogLevel.Information, message, null, properties);
    }
    logWarning(message, properties) {
        this.log(types_1.LmtLogLevel.Warning, message, null, properties);
    }
    logError(message, exception, properties) {
        this.log(types_1.LmtLogLevel.Error, message, exception, properties);
    }
    logCritical(message, exception, properties) {
        this.log(types_1.LmtLogLevel.Critical, message, exception, properties);
    }
    async flushBatch() {
        if (this.flushLock)
            return;
        this.flushLock = true;
        try {
            const logs = [];
            while (this.logBatch.length > 0) {
                const log = this.logBatch.shift();
                if (log)
                    logs.push(log);
            }
            if (logs.length > 0) {
                await this.serviceBusSender.sendLogs(logs);
            }
        }
        finally {
            this.flushLock = false;
        }
    }
    async dispose() {
        if (this.disposed)
            return;
        if (this.flushTimer) {
            clearInterval(this.flushTimer);
        }
        await this.flushBatch();
        await this.serviceBusSender.dispose();
        this.disposed = true;
    }
}
exports.LmtLogger = LmtLogger;
//# sourceMappingURL=lmt-logger.js.map