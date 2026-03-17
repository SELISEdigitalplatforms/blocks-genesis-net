namespace Blocks.Genesis
{
    /// <summary>
    /// Represents general message configuration for services using messaging infrastructure.
    /// </summary>
    public class MessageConfiguration
    {
        /// <summary>
        /// Gets or sets the connection string for the message broker.
        /// </summary>
        public string? Connection { get; set; }

        /// <summary>
        /// Gets or sets the unique name of the service using this configuration.
        /// </summary>
        public string? ServiceName { get; set; }

        /// <summary>
        /// Gets or sets the Azure Service Bus specific configuration.
        /// </summary>
        public AzureServiceBusConfiguration? AzureServiceBusConfiguration { get; set; }
        public RabbitMqConfiguration? RabbitMqConfiguration { get; set; }

        /// <summary>
        /// Generates a subscription name based on the topic name and service name.
        /// </summary>
        /// <param name="topicName">The name of the topic.</param>
        /// <returns>A formatted subscription name.</returns>
        public string GetSubscriptionName(string topicName)
        {
            return $"{topicName}_sub_{ServiceName}";
        }
    }

    /// <summary>
    /// Configuration for Azure Service Bus entities such as queues and topics.
    /// </summary>
    public class AzureServiceBusConfiguration
    {
        private List<string> _queues = [];
        private List<string> _topics = [];

        /// <summary>
        /// Gets or sets the list of queue names.
        /// </summary>
        public List<string> Queues
        {
            get => _queues;
            set => _queues = value?
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.ToLowerInvariant())
                .ToList() ?? _queues;
        }

        /// <summary>
        /// Gets or sets the list of topic names.
        /// </summary>
        public List<string> Topics
        {
            get => _topics;
            set => _topics = value?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.ToLowerInvariant())
                .ToList() ?? _topics;
        }

        public long QueueMaxSizeInMegabytes { get; set; } = 1024;
        public int QueueMaxDeliveryCount { get; set; } = 2;
        public int QueuePrefetchCount { get; set; } = 10;
        public TimeSpan QueueDefaultMessageTimeToLive { get; set; } = TimeSpan.FromDays(7);

        public int TopicPrefetchCount { get; set; } = 10;
        public long TopicMaxSizeInMegabytes { get; set; } = 1024;
        public TimeSpan TopicDefaultMessageTimeToLive { get; set; } = TimeSpan.FromDays(30);

        public int TopicSubscriptionMaxDeliveryCount { get; set; } = 2;
        public TimeSpan TopicSubscriptionDefaultMessageTimeToLive { get; set; } = TimeSpan.FromDays(7);
        public int MaxConcurrentCalls { get; set; } = 5;
        public int MaxMessageProcessingTimeInMinutes { get; set; } = 60;
        public int MessageLockRenewalIntervalSeconds { get; set; } = 270;
    }

    /// <summary>
    /// Configuration for RabbitMQ consumer queues and topics.
    /// </summary>
    public class RabbitMqConfiguration
    {
        /// <summary>
        /// Gets or sets the list of consumer queue subscriptions.
        /// </summary>
        public List<ConsumerSubscription> ConsumerSubscriptions { get; set; } = new();

        /// <summary>
        /// Gets or sets the default TTL (Time-To-Live) in seconds for RabbitMQ messages.
        /// </summary>
        public int MessageTtlSeconds { get; set; }
    }

    /// <summary>
    /// Represents a subscription to a queue or a topic via exchange in RabbitMQ.
    /// </summary>
    public class ConsumerSubscription
    {
        public string QueueName { get; }
        public string ExchangeName { get; }
        public ushort PrefetchCount { get; }
        public string ExchangeType { get; }
        public string RoutingKey { get; }
        public bool ShouldBypassAuthorization { get; }
        public bool Durable { get; }
        public bool ParallelProcessing { get; }

        public ConsumerSubscription(
            string queueName,
            string exchangeName,
            ushort prefetchCount,
            bool parallelProcessing = false,
            string exchangeType = "fanout",
            string routingKey = "",
            bool shouldBypassAuthorization = false,
            bool durable = true)
        {
            QueueName = queueName;
            ExchangeName = exchangeName;
            PrefetchCount = prefetchCount;
            ExchangeType = exchangeType;
            RoutingKey = routingKey;
            ShouldBypassAuthorization = shouldBypassAuthorization;
            Durable = durable;
        }

        /// <summary>
        /// Creates a subscription that binds directly to a queue.
        /// </summary>
        public static ConsumerSubscription BindToQueue(string queueName, ushort prefetchCount = 5) =>
            new(queueName, string.Empty, prefetchCount);

        /// <summary>
        /// Creates a subscription that binds a queue via an exchange.
        /// </summary>
        public static ConsumerSubscription BindToQueueViaExchange(
            string queueName,
            string exchangeName,
            ushort prefetchCount = 5,
            bool parallelProcessing = false) =>
            new(queueName, exchangeName, prefetchCount, parallelProcessing);

        /// <summary>
        /// Creates a detailed subscription binding a queue to an exchange with routing information.
        /// </summary>
        public static ConsumerSubscription BindToQueueViaExchange(
            string queueName,
            string exchangeName,
            ushort prefetchCount,
            string exchangeType,
            string routingKey,
            bool parallelProcessing = false,
            bool shouldBypassAuthorization = false,
            bool durable = true) =>
            new(queueName, exchangeName, prefetchCount, parallelProcessing, exchangeType, routingKey, shouldBypassAuthorization, durable);
    }
}
