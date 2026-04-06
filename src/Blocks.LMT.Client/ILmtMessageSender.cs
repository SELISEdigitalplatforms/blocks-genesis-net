namespace SeliseBlocks.LMT.Client
{
    /// <summary>
    /// Interface for sending log and trace messages to a message transport (Service Bus or RabbitMQ).
    /// </summary>
    public interface ILmtMessageSender : IDisposable
    {
        /// <summary>
        /// Sends a batch of logs asynchronously.
        /// </summary>
        Task SendLogsAsync(List<LogData> logs, int retryCount = 0);

        /// <summary>
        /// Sends trace batches grouped by tenant asynchronously.
        /// </summary>
        Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0);
    }
}
