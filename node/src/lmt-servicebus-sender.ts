import { ServiceBusClient, ServiceBusSender } from '@azure/service-bus';
import {
  LogData,
  TraceData,
  FailedLogBatch,
  FailedTraceBatch,
  LmtConstants
} from './types';

export class LmtServiceBusSender {
  private serviceName: string;
  private maxRetries: number;
  private maxFailedBatches: number;
  private failedLogBatches: FailedLogBatch[] = [];
  private failedTraceBatches: FailedTraceBatch[] = [];
  private retryTimer?: NodeJS.Timeout;
  private serviceBusClient?: ServiceBusClient;
  private serviceBusSender?: ServiceBusSender;
  private retryLock = false;
  private disposed = false;

  constructor(
    serviceName: string,
    serviceBusConnectionString: string,
    maxRetries: number = 3,
    maxFailedBatches: number = 100
  ) {
    this.serviceName = serviceName;
    this.maxRetries = maxRetries;
    this.maxFailedBatches = maxFailedBatches;

    if (serviceBusConnectionString) {
      this.serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
      this.serviceBusSender = this.serviceBusClient.createSender(
        LmtConstants.getTopicName(serviceName)
      );
    }

    this.retryTimer = setInterval(() => {
      this.retryFailedBatches().catch(console.error);
    }, 30000);
  }

  async sendLogs(logs: LogData[], retryCount: number = 0): Promise<void> {
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
          correlationId: LmtConstants.LOG_SUBSCRIPTION,
          applicationProperties: {
            serviceName: this.serviceName,
            timestamp: timestamp.toISOString(),
            source: 'LogsSender',
            type: 'logs'
          }
        };

        await this.serviceBusSender.sendMessages(message);
        return;
      } catch (ex: any) {
        console.log(
          `Exception sending logs to Service Bus: ${ex.message}, Retry: ${currentRetry}/${this.maxRetries}`
        );
      }

      currentRetry++;

      if (currentRetry <= this.maxRetries) {
        const delay = Math.pow(2, currentRetry - 1) * 1000;
        await this.sleep(delay);
      }
    }

    // Queue for later retry
    if (this.failedLogBatches.length < this.maxFailedBatches) {
      const failedBatch: FailedLogBatch = {
        logs,
        retryCount: retryCount + 1,
        nextRetryTime: new Date(Date.now() + Math.pow(2, retryCount) * 60000)
      };

      this.failedLogBatches.push(failedBatch);
      console.log(
        `Queued log batch for later retry. Failed batches in queue: ${this.failedLogBatches.length}`
      );
    } else {
      console.log(
        `Failed log batch queue is full (${this.maxFailedBatches}). Dropping batch.`
      );
    }
  }

  async sendTraces(
    tenantBatches: Record<string, TraceData[]>,
    retryCount: number = 0
  ): Promise<void> {
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
          correlationId: LmtConstants.TRACE_SUBSCRIPTION,
          applicationProperties: {
            serviceName: this.serviceName,
            timestamp: timestamp.toISOString(),
            source: 'TracesSender',
            type: 'traces'
          }
        };

        await this.serviceBusSender.sendMessages(message);
        return;
      } catch (ex: any) {
        console.log(
          `Exception sending traces to Service Bus: ${ex.message}, Retry: ${currentRetry}/${this.maxRetries}`
        );
      }

      currentRetry++;

      if (currentRetry <= this.maxRetries) {
        const delay = Math.pow(2, currentRetry - 1) * 1000;
        await this.sleep(delay);
      }
    }

    // Queue for later retry
    if (this.failedTraceBatches.length < this.maxFailedBatches) {
      const failedBatch: FailedTraceBatch = {
        tenantBatches,
        retryCount: retryCount + 1,
        nextRetryTime: new Date(Date.now() + Math.pow(2, retryCount) * 60000)
      };

      this.failedTraceBatches.push(failedBatch);
      console.log(
        `Queued trace batch for later retry. Failed batches in queue: ${this.failedTraceBatches.length}`
      );
    } else {
      console.log(
        `Failed trace batch queue is full (${this.maxFailedBatches}). Dropping batch.`
      );
    }
  }

  private async retryFailedBatches(): Promise<void> {
    if (this.retryLock) return;

    this.retryLock = true;
    try {
      const now = new Date();

      await this.retryFailedLogs(now);
      await this.retryFailedTraces(now);
    } finally {
      this.retryLock = false;
    }
  }

  private async retryFailedLogs(now: Date): Promise<void> {
    const batchesToRetry: FailedLogBatch[] = [];
    const batchesToRequeue: FailedLogBatch[] = [];

    for (const batch of this.failedLogBatches) {
      if (batch.nextRetryTime <= now) {
        batchesToRetry.push(batch);
      } else {
        batchesToRequeue.push(batch);
      }
    }

    this.failedLogBatches = batchesToRequeue;

    for (const failedBatch of batchesToRetry) {
      if (failedBatch.retryCount >= this.maxRetries) {
        console.log(
          `Log batch exceeded max retries (${this.maxRetries}). Dropping batch with ${failedBatch.logs.length} logs.`
        );
        continue;
      }

      console.log(
        `Retrying failed log batch (Attempt ${failedBatch.retryCount + 1}/${this.maxRetries})`
      );
      await this.sendLogs(failedBatch.logs, failedBatch.retryCount);
    }
  }

  private async retryFailedTraces(now: Date): Promise<void> {
    const batchesToRetry: FailedTraceBatch[] = [];
    const batchesToRequeue: FailedTraceBatch[] = [];

    for (const batch of this.failedTraceBatches) {
      if (batch.nextRetryTime <= now) {
        batchesToRetry.push(batch);
      } else {
        batchesToRequeue.push(batch);
      }
    }

    this.failedTraceBatches = batchesToRequeue;

    for (const failedBatch of batchesToRetry) {
      if (failedBatch.retryCount >= this.maxRetries) {
        console.log(
          `Trace batch exceeded max retries (${this.maxRetries}). Dropping batch.`
        );
        continue;
      }

      console.log(
        `Retrying failed trace batch (Attempt ${failedBatch.retryCount + 1}/${this.maxRetries})`
      );
      await this.sendTraces(
        failedBatch.tenantBatches,
        failedBatch.retryCount
      );
    }
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;

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

  private sleep(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  private generateGuid(): string {
    return 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'.replace(/x/g, () =>
      ((Math.random() * 16) | 0).toString(16)
    );
  }
}