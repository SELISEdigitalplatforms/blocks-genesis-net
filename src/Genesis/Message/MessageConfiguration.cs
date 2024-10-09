namespace Blocks.Genesis
{
    public class MessageConfiguration
    {
        private List<string> _queues = new List<string>();
        private List<string> _topics = new List<string>();

        public string Connection { get; set; }
        public List<string> Queues
        {
            get => _queues;

            set
            {
                _queues = value?.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.ToLower()).ToList() ?? _queues;
            }
        }

        public long QueueMaxSizeInMegabytes { get; set; } = 1024;
        public int QueueMaxDeliveryCount { get; set; } = 5;
        public int QueuePrefetchCount { get; set; } = 5;
        public TimeSpan QueueDefaultMessageTimeToLive { get; set; } = TimeSpan.FromMinutes(60 * 24 * 7);

        public List<string> Topics
        {
            get => _topics;

            set
            {
                _topics = value?.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.ToLower()).ToList() ?? _topics;
            }
        }

        public int TopicPrefetchCount { get; set; } = 5;
        public long TopicMaxSizeInMegabytes { get; set; } = 1024;
        public TimeSpan TopicDefaultMessageTimeToLive { get; set; } = TimeSpan.FromMinutes(60 * 24 * 30);
        public int TopicSubscriptionMaxDeliveryCount { get; set; } = 5;
        public TimeSpan TopicSubscriptionDefaultMessageTimeToLive { get; set; } = TimeSpan.FromMinutes(60 * 24 * 7);
        public string ServiceName { get; set; }

        public string GetSubscriptionName(string topicName)
        {
            return $"{topicName}_sub_{ServiceName}";
        }

    }
}
