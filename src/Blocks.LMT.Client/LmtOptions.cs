namespace SeliseBlocks.LMT.Client
{
    public class LmtOptions
    {
        private string _serviceId = string.Empty;
        private string _connectionString = string.Empty;
        private int _logBatchSize = 100;
        private int _traceBatchSize = 1000;
        private int _flushIntervalSeconds = 5;
        private int _maxRetries = 3;
        private int _maxFailedBatches = 100;

        public string ServiceId
        {
            get => _serviceId;
            set => _serviceId = value ?? string.Empty;
        }

        public string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value ?? string.Empty;
        }

        public int LogBatchSize
        {
            get => _logBatchSize;
            set => _logBatchSize = value > 0 ? value : 100;
        }

        public int TraceBatchSize
        {
            get => _traceBatchSize;
            set => _traceBatchSize = value > 0 ? value : 1000;
        }

        public int FlushIntervalSeconds
        {
            get => _flushIntervalSeconds;
            set => _flushIntervalSeconds = value > 0 ? value : 5;
        }

        public int MaxRetries
        {
            get => _maxRetries;
            set => _maxRetries = Math.Max(0, Math.Min(value, 10)); // Cap at 10 retries
        }

        public int MaxFailedBatches
        {
            get => _maxFailedBatches;
            set => _maxFailedBatches = Math.Max(1, value);
        }

        public bool EnableLogging { get; set; } = true;
        public bool EnableTracing { get; set; } = true;

        public string XBlocksKey { get; set; } = string.Empty;

        /// <summary>
        /// Validates that required options are configured.
        /// Throws InvalidOperationException if validation fails.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ServiceId))
                throw new InvalidOperationException("ServiceId is required");

            if (string.IsNullOrWhiteSpace(ConnectionString))
                throw new InvalidOperationException("ConnectionString is required");

            // Validate connection string format is supported
            if (!LmtTransportHelper.IsValidConnectionString(ConnectionString))
                throw new InvalidOperationException("ConnectionString must be a valid Azure Service Bus or RabbitMQ connection string");
        }
    }

    public class LmtConstants
    {
        public const string LogSubscription = "blocks-lmt-service-logs";
        public const string TraceSubscription = "blocks-lmt-service-traces";
        public const string RabbitMqLogsRoutingKey = "logs";
        public const string RabbitMqTracesRoutingKey = "traces";
        public static string GetTopicName(string serviceName)
        {
            return "lmt-" + serviceName;
        }
        public static string GetRabbitMqExchangeName(string serviceName)
        {
            return "lmt-" + serviceName;
        }
    }
 
}