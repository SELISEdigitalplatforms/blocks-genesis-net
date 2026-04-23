"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.LmtConstants = exports.LmtLogLevel = void 0;
var LmtLogLevel;
(function (LmtLogLevel) {
    LmtLogLevel[LmtLogLevel["Trace"] = 0] = "Trace";
    LmtLogLevel[LmtLogLevel["Debug"] = 1] = "Debug";
    LmtLogLevel[LmtLogLevel["Information"] = 2] = "Information";
    LmtLogLevel[LmtLogLevel["Warning"] = 3] = "Warning";
    LmtLogLevel[LmtLogLevel["Error"] = 4] = "Error";
    LmtLogLevel[LmtLogLevel["Critical"] = 5] = "Critical";
})(LmtLogLevel || (exports.LmtLogLevel = LmtLogLevel = {}));
class LmtConstants {
    static getTopicName(serviceName) {
        return `lmt-${serviceName}`;
    }
}
exports.LmtConstants = LmtConstants;
LmtConstants.LOG_SUBSCRIPTION = 'blocks-lmt-service-logs';
LmtConstants.TRACE_SUBSCRIPTION = 'blocks-lmt-service-traces';
//# sourceMappingURL=types.js.map