namespace Blocks.Genesis
{
    public interface IBlocksSecret
    {
        public string CacheConnectionString { get; set; }
        public string LogFilesRootDirectory { get; set; }
        public string StorageBasePath { get; set; }
        public string BlocksAuditLogQueueName { get; set; }
        public string TokenIssuer { get; set; }
        public string MessageConnectionString { get; set; }
        public string LogConnectionString { get; set; }
        public string MetricConnectionString { get; set; }
        public string TraceConnectionString { get; set; }
    }
}
