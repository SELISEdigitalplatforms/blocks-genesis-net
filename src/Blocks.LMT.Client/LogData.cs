namespace Blocks.LMT.Client
{
    public class LogData
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Exception { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new();
        public string TenantId { get; set; } = string.Empty;
    }

    public class FailedLogBatch
    {
        public List<LogData> Logs { get; set; } = new();
        public int RetryCount { get; set; }
        public DateTime NextRetryTime { get; set; }
    }
}
