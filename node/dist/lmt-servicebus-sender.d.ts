import { LogData, TraceData } from './types';
export declare class LmtServiceBusSender {
    private serviceName;
    private maxRetries;
    private maxFailedBatches;
    private failedLogBatches;
    private failedTraceBatches;
    private retryTimer?;
    private serviceBusClient?;
    private serviceBusSender?;
    private retryLock;
    private disposed;
    constructor(serviceName: string, serviceBusConnectionString: string, maxRetries?: number, maxFailedBatches?: number);
    sendLogs(logs: LogData[], retryCount?: number): Promise<void>;
    sendTraces(tenantBatches: Record<string, TraceData[]>, retryCount?: number): Promise<void>;
    private retryFailedBatches;
    private retryFailedLogs;
    private retryFailedTraces;
    dispose(): Promise<void>;
    private sleep;
    private generateGuid;
}
//# sourceMappingURL=lmt-servicebus-sender.d.ts.map