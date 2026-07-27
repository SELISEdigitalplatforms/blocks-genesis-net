using System.Collections.Concurrent;

namespace SeliseBlocks.LMT.Client;

/// <summary>
/// Shared retry pipeline for failed log and trace batches, used by the
/// transport senders so the batching, requeue and retry logic exists once.
/// </summary>
internal static class LmtFailedBatchRetryHelper
{
    /// <summary>
    /// Runs one retry pass over the failed log and trace batch queues,
    /// skipping the pass entirely when another one is already in progress.
    /// </summary>
    public static async Task RetryFailedBatchesAsync(
        SemaphoreSlim retrySemaphore,
        Func<DateTime, Task> retryFailedLogsAsync,
        Func<DateTime, Task> retryFailedTracesAsync)
    {
        if (!await retrySemaphore.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            var now = DateTime.UtcNow;

            await retryFailedLogsAsync(now).ConfigureAwait(false);
            await retryFailedTracesAsync(now).ConfigureAwait(false);
        }
        finally
        {
            retrySemaphore.Release();
        }
    }

    /// <summary>
    /// Drains the queue, requeues batches whose retry time has not arrived,
    /// drops batches that exceeded the retry budget and resends the rest in order.
    /// </summary>
    public static async Task RetryDueBatchesAsync<TBatch>(
        ConcurrentQueue<TBatch> failedBatches,
        DateTime now,
        int maxRetries,
        Func<TBatch, DateTime> getNextRetryTime,
        Func<TBatch, int> getRetryCount,
        Action<TBatch> onRetriesExceeded,
        Func<TBatch, Task> resendAsync)
    {
        var batchesToRetry = new List<TBatch>();
        var batchesToRequeue = new List<TBatch>();

        while (failedBatches.TryDequeue(out var failedBatch))
        {
            if (getNextRetryTime(failedBatch) <= now)
                batchesToRetry.Add(failedBatch);
            else
                batchesToRequeue.Add(failedBatch);
        }

        foreach (var batch in batchesToRequeue)
        {
            failedBatches.Enqueue(batch);
        }

        foreach (var failedBatch in batchesToRetry)
        {
            if (getRetryCount(failedBatch) >= maxRetries)
            {
                onRetriesExceeded(failedBatch);
                continue;
            }

            await resendAsync(failedBatch).ConfigureAwait(false);
        }
    }
}
