import { LmtOptions, LmtLogLevel, ILmtLogger } from './types';
export declare class LmtLogger implements ILmtLogger {
    private options;
    private logBatch;
    private flushTimer?;
    private serviceBusSender;
    private flushLock;
    private disposed;
    constructor(options: LmtOptions);
    log(level: LmtLogLevel, message: string, exception?: Error | null, properties?: Record<string, any>): void;
    logTrace(message: string, properties?: Record<string, any>): void;
    logDebug(message: string, properties?: Record<string, any>): void;
    logInformation(message: string, properties?: Record<string, any>): void;
    logWarning(message: string, properties?: Record<string, any>): void;
    logError(message: string, exception?: Error | null, properties?: Record<string, any>): void;
    logCritical(message: string, exception?: Error | null, properties?: Record<string, any>): void;
    private flushBatch;
    dispose(): Promise<void>;
}
//# sourceMappingURL=lmt-logger.d.ts.map