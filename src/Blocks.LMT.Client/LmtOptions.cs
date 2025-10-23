namespace SeliseBlocks.LMT.Client
{
    public class LmtOptions
    {
        public string ServiceId { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        public int LogBatchSize { get; set; } = 100;
        public int TraceBatchSize { get; set; } = 1000;
        public int FlushIntervalSeconds { get; set; } = 5;
        public int MaxRetries { get; set; } = 3;
        public int MaxFailedBatches { get; set; } = 100;
        public bool EnableLogging { get; set; } = true;
        public bool EnableTracing { get; set; } = true;
        public string XBlocksKey { get; set; } = string.Empty;
    }

    public class LmtConstants
    {
        public const string LogSubscription = "blocks-lmt-service-logs";
        public const string TraceSubscription = "blocks-lmt-service-traces";

        public static string GetTopicName(string serviceName)
        {
            return "lmt-" + serviceName;
        }
    }
 
}