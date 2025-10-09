namespace Blocks.LMT.Client
{
    public class LmtOptions
    {
        public string ServiceName { get; set; } = string.Empty;
        public string LogsServiceBusConnectionString { get; set; } = string.Empty;
        public string TracesServiceBusConnectionString { get; set; } = string.Empty;
        public int LogBatchSize { get; set; } = 100;
        public int TraceBatchSize { get; set; } = 1000;
        public int FlushIntervalSeconds { get; set; } = 5;
        public int MaxRetries { get; set; } = 3;
        public int MaxFailedBatches { get; set; } = 100;
        public bool EnableLogging { get; set; } = true;
        public bool EnableTracing { get; set; } = true;
    }

    public class LmtConstants
    {
        public const string LogTopic = "blocks-lmt-sevice-logs";
        public const string TraceTopic = "blocks-lmt-sevice-traces";
    }
}