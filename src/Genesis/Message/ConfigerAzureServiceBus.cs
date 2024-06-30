using Azure.Messaging.ServiceBus.Administration;

namespace Blocks.Genesis
{
    public static class ConfigerAzureServiceBus
    {
        private static ServiceBusAdministrationClient _adminClient;
        private static MessageConfiguration _messageConfiguration;
        public static async Task ConfigerMessagesAsync(MessageConfiguration messageConfiguration)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(messageConfiguration.Connection))
                {
                    Console.WriteLine("Error in creation of message queues/topics");
                    return;
                }
                _adminClient = new ServiceBusAdministrationClient(messageConfiguration.Connection);
                _messageConfiguration = messageConfiguration;

                await CreateQueuesAsync();

                await CreateTopicAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        static async Task CreateQueuesAsync()
        {
            try
            {
                foreach (var queueName in _messageConfiguration.Queues)
                {

                    var isExist = await CheckQueueExistsAsync(queueName);
                    if (isExist) return;

                    var createQueueOptions = new CreateQueueOptions(queueName);
                    createQueueOptions.MaxSizeInMegabytes = _messageConfiguration.QueueMaxSizeInMegabytes;
                    createQueueOptions.MaxDeliveryCount = _messageConfiguration.QueueMaxDeliveryCount;
                    createQueueOptions.DefaultMessageTimeToLive = _messageConfiguration.QueueDefaultMessageTimeToLive;
                    createQueueOptions.LockDuration = TimeSpan.FromSeconds(30);

                    _adminClient.CreateQueueAsync(createQueueOptions); // don't await it need synchronization
                }

            }
            catch (Exception ex)
            {

                Console.WriteLine(ex);
                throw;
            }
        }

        async static Task<bool> CheckQueueExistsAsync(string queue)
        {
            return await _adminClient.QueueExistsAsync(queue);
        }

        static async Task CreateTopicAsync()
        {
            try
            {
                foreach (var topicName in _messageConfiguration.Topics)
                {

                    var isExist = await CheckTopicExistsAsync(topicName);
                    if (isExist) return;

                    var createTopicOptions = new CreateTopicOptions(topicName);
                    createTopicOptions.MaxSizeInMegabytes = _messageConfiguration.TopicMaxSizeInMegabytes;
                    createTopicOptions.DefaultMessageTimeToLive = _messageConfiguration.TopicDefaultMessageTimeToLive;

                    _adminClient.CreateTopicAsync(createTopicOptions); // don't await it need synchronization

                    CreateTopicSubscriptionAsync(topicName); // don't await it need synchronization
                }

            }
            catch (Exception ex)
            {

                Console.WriteLine(ex);
                throw;
            }
        }

        async static Task<bool> CheckTopicExistsAsync(string topicName)
        {
            return await _adminClient.TopicExistsAsync(topicName);
        }

        static async Task CreateTopicSubscriptionAsync(string topicName)
        {
            try
            {

                var isExist = await CheckSubscriptionExistsAsync(topicName, _messageConfiguration.GetSubscriptionName(topicName));
                if (isExist) return;

                var createTopicSubscriptionOptions = new CreateSubscriptionOptions(topicName, _messageConfiguration.GetSubscriptionName(topicName));
                createTopicSubscriptionOptions.MaxDeliveryCount = _messageConfiguration.TopicSubscriptionMaxDeliveryCount;
                createTopicSubscriptionOptions.DefaultMessageTimeToLive = _messageConfiguration.TopicSubscriptionDefaultMessageTimeToLive;
                createTopicSubscriptionOptions.LockDuration = TimeSpan.FromSeconds(30);

                _adminClient.CreateSubscriptionAsync(createTopicSubscriptionOptions); // don't await it need synchronization

            }
            catch (Exception ex)
            {

                Console.WriteLine(ex);
                throw;
            }
        }

        async static Task<bool> CheckSubscriptionExistsAsync(string topicName, string subscriptionName)
        {
            return await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName);
        }



    }
}
