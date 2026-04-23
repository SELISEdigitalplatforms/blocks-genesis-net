"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.LmtServiceBusSender = void 0;
const service_bus_1 = require("@azure/service-bus");
const types_1 = require("./types");
class LmtServiceBusSender {
    constructor(serviceName, serviceBusConnectionString, maxRetries = 3, maxFailedBatches = 100) {
        this.failedLogBatches = [];
        this.failedTraceBatches = [];
        this.retryLock = false;
        this.disposed = false;
        this.serviceName = serviceName;
        this.maxRetries = maxRetries;
        this.maxFailedBatches = maxFailedBatches;
        if (serviceBusConnectionString) {
            this.serviceBusClient = new service_bus_1.ServiceBusClient(serviceBusConnectionString);
            this.serviceBusSender = this.serviceBusClient.createSender(types_1.LmtConstants.getTopicName(serviceName));
        }
        this.retryTimer = setInterval(() => {
            this.retryFailedBatches().catch(console.error);
        }, 30000);
    }
    async sendLogs(logs, retryCount = 0) {
        if (!this.serviceBusSender) {
            console.log('Service Bus sender not initialized');
            return;
        }
        let currentRetry = 0;
        while (currentRetry <= this.maxRetries) {
            try {
                const payload = {
                    Type: 'logs',
                    ServiceName: this.serviceName,
                    Data: logs
                };
                const timestamp = new Date();
                const messageId = `logs_${this.serviceName}_${timestamp.toISOString().replace(/[-:]/g, '').replace(/\..+/, '')}_${this.generateGuid()}`;
                const message = {
                    body: payload,
                    contentType: 'application/json',
                    messageId,
                    correlationId: types_1.LmtConstants.LOG_SUBSCRIPTION,
                    applicationProperties: {
                        serviceName: this.serviceName,
                        timestamp: timestamp.toISOString(),
                        source: 'LogsSender',
                        type: 'logs'
                    }
                };
                await this.serviceBusSender.sendMessages(message);
                return;
            }
            catch (ex) {
                console.log(`Exception sending logs to Service Bus: ${ex.message}, Retry: ${currentRetry}/${this.maxRetries}`);
            }
            currentRetry++;
            if (currentRetry <= this.maxRetries) {
                const delay = Math.pow(2, currentRetry - 1) * 1000;
                await this.sleep(delay);
            }
        }
        // Queue for later retry
        if (this.failedLogBatches.length < this.maxFailedBatches) {
            const failedBatch = {
                logs,
                retryCount: retryCount + 1,
                nextRetryTime: new Date(Date.now() + Math.pow(2, retryCount) * 60000)
            };
            this.failedLogBatches.push(failedBatch);
            console.log(`Queued log batch for later retry. Failed batches in queue: ${this.failedLogBatches.length}`);
        }
        else {
            console.log(`Failed log batch queue is full (${this.maxFailedBatches}). Dropping batch.`);
        }
    }
    async sendTraces(tenantBatches, retryCount = 0) {
        if (!this.serviceBusSender) {
            console.log('Service Bus sender not initialized');
            return;
        }
        let currentRetry = 0;
        while (currentRetry <= this.maxRetries) {
            try {
                const payload = {
                    Type: 'traces',
                    ServiceName: this.serviceName,
                    Data: tenantBatches
                };
                const timestamp = new Date();
                const messageId = `traces_${this.serviceName}_${timestamp.toISOString().replace(/[-:]/g, '').replace(/\..+/, '')}_${this.generateGuid()}`;
                const message = {
                    body: payload,
                    contentType: 'application/json',
                    messageId,
                    correlationId: types_1.LmtConstants.TRACE_SUBSCRIPTION,
                    applicationProperties: {
                        serviceName: this.serviceName,
                        timestamp: timestamp.toISOString(),
                        source: 'TracesSender',
                        type: 'traces'
                    }
                };
                await this.serviceBusSender.sendMessages(message);
                return;
            }
            catch (ex) {
                console.log(`Exception sending traces to Service Bus: ${ex.message}, Retry: ${currentRetry}/${this.maxRetries}`);
            }
            currentRetry++;
            if (currentRetry <= this.maxRetries) {
                const delay = Math.pow(2, currentRetry - 1) * 1000;
                await this.sleep(delay);
            }
        }
        // Queue for later retry
        if (this.failedTraceBatches.length < this.maxFailedBatches) {
            const failedBatch = {
                tenantBatches,
                retryCount: retryCount + 1,
                nextRetryTime: new Date(Date.now() + Math.pow(2, retryCount) * 60000)
            };
            this.failedTraceBatches.push(failedBatch);
            console.log(`Queued trace batch for later retry. Failed batches in queue: ${this.failedTraceBatches.length}`);
        }
        else {
            console.log(`Failed trace batch queue is full (${this.maxFailedBatches}). Dropping batch.`);
        }
    }
    async retryFailedBatches() {
        if (this.retryLock)
            return;
        this.retryLock = true;
        try {
            const now = new Date();
            await this.retryFailedLogs(now);
            await this.retryFailedTraces(now);
        }
        finally {
            this.retryLock = false;
        }
    }
    async retryFailedLogs(now) {
        const batchesToRetry = [];
        const batchesToRequeue = [];
        for (const batch of this.failedLogBatches) {
            if (batch.nextRetryTime <= now) {
                batchesToRetry.push(batch);
            }
            else {
                batchesToRequeue.push(batch);
            }
        }
        this.failedLogBatches = batchesToRequeue;
        for (const failedBatch of batchesToRetry) {
            if (failedBatch.retryCount >= this.maxRetries) {
                console.log(`Log batch exceeded max retries (${this.maxRetries}). Dropping batch with ${failedBatch.logs.length} logs.`);
                continue;
            }
            console.log(`Retrying failed log batch (Attempt ${failedBatch.retryCount + 1}/${this.maxRetries})`);
            await this.sendLogs(failedBatch.logs, failedBatch.retryCount);
        }
    }
    async retryFailedTraces(now) {
        const batchesToRetry = [];
        const batchesToRequeue = [];
        for (const batch of this.failedTraceBatches) {
            if (batch.nextRetryTime <= now) {
                batchesToRetry.push(batch);
            }
            else {
                batchesToRequeue.push(batch);
            }
        }
        this.failedTraceBatches = batchesToRequeue;
        for (const failedBatch of batchesToRetry) {
            if (failedBatch.retryCount >= this.maxRetries) {
                console.log(`Trace batch exceeded max retries (${this.maxRetries}). Dropping batch.`);
                continue;
            }
            console.log(`Retrying failed trace batch (Attempt ${failedBatch.retryCount + 1}/${this.maxRetries})`);
            await this.sendTraces(failedBatch.tenantBatches, failedBatch.retryCount);
        }
    }
    async dispose() {
        if (this.disposed)
            return;
        if (this.retryTimer) {
            clearInterval(this.retryTimer);
        }
        await this.retryFailedBatches();
        if (this.serviceBusSender) {
            await this.serviceBusSender.close();
        }
        if (this.serviceBusClient) {
            await this.serviceBusClient.close();
        }
        this.disposed = true;
    }
    sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }
    generateGuid() {
        return 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'.replace(/x/g, () => ((Math.random() * 16) | 0).toString(16));
    }
}
exports.LmtServiceBusSender = LmtServiceBusSender;
//# sourceMappingURL=lmt-servicebus-sender.js.map