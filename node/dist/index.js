"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.LmtConstants = exports.LmtLogLevel = exports.LmtTraceExporter = exports.LmtServiceBusSender = exports.LmtLogger = void 0;
exports.createLmtLogger = createLmtLogger;
exports.createLmtTraceExporter = createLmtTraceExporter;
exports.createLmtSpanProcessor = createLmtSpanProcessor;
var lmt_logger_1 = require("./lmt-logger");
Object.defineProperty(exports, "LmtLogger", { enumerable: true, get: function () { return lmt_logger_1.LmtLogger; } });
var lmt_servicebus_sender_1 = require("./lmt-servicebus-sender");
Object.defineProperty(exports, "LmtServiceBusSender", { enumerable: true, get: function () { return lmt_servicebus_sender_1.LmtServiceBusSender; } });
var lmt_trace_processor_1 = require("./lmt-trace-processor");
Object.defineProperty(exports, "LmtTraceExporter", { enumerable: true, get: function () { return lmt_trace_processor_1.LmtTraceExporter; } });
var types_1 = require("./types");
Object.defineProperty(exports, "LmtLogLevel", { enumerable: true, get: function () { return types_1.LmtLogLevel; } });
Object.defineProperty(exports, "LmtConstants", { enumerable: true, get: function () { return types_1.LmtConstants; } });
const lmt_logger_2 = require("./lmt-logger");
const lmt_trace_processor_2 = require("./lmt-trace-processor");
const sdk_trace_base_1 = require("@opentelemetry/sdk-trace-base");
function createLmtLogger(options) {
    return new lmt_logger_2.LmtLogger(options);
}
function createLmtTraceExporter(options) {
    return new lmt_trace_processor_2.LmtTraceExporter(options);
}
// Helper to create properly configured span processor (recommended)
function createLmtSpanProcessor(options) {
    const exporter = new lmt_trace_processor_2.LmtTraceExporter(options);
    return new sdk_trace_base_1.BatchSpanProcessor(exporter, {
        maxQueueSize: options.traceBatchSize || 1000,
        scheduledDelayMillis: (options.flushIntervalSeconds || 5) * 1000
    });
}
//# sourceMappingURL=index.js.map