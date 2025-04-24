using Azure.Messaging.ServiceBus.Administration;

namespace Blocks.Genesis
{
    public static class ConfigerAzureServiceBus
    {
        private static ServiceBusAdministrationClient _adminClient;
        private static MessageConfiguration _messageConfiguration;

        public static async Task ConfigerQueueAndTopicAsync(MessageConfiguration messageConfiguration)
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

                var queueCreationTask = CreateQueuesAsync();
                var topicCreationTask = CreateTopicAsync();
                await Task.WhenAll(queueCreationTask, topicCreationTask);
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
                var tasks = new List<Task>();   
                
                foreach (var queueName in _messageConfiguration?.AzureServiceBusConfiguration?.Queues ?? new())
                {

                    var isExist = await CheckQueueExistsAsync(queueName);
                    if (isExist) continue;

                    var createQueueOptions = new CreateQueueOptions(queueName);
                    createQueueOptions.MaxSizeInMegabytes = _messageConfiguration?.AzureServiceBusConfiguration?.QueueMaxSizeInMegabytes ?? 1024;
                    createQueueOptions.MaxDeliveryCount = _messageConfiguration?.AzureServiceBusConfiguration?.QueueMaxDeliveryCount ?? 5;
                    createQueueOptions.DefaultMessageTimeToLive = _messageConfiguration?.AzureServiceBusConfiguration?.QueueDefaultMessageTimeToLive ?? TimeSpan.FromDays(7);
                    createQueueOptions.LockDuration = TimeSpan.FromHours(1);

                    tasks.Add(_adminClient.CreateQueueAsync(createQueueOptions));
                }

                await Task.WhenAll(tasks);

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
                var tasks = new List<Task>();

                foreach (var topicName in _messageConfiguration?.AzureServiceBusConfiguration?.Topics ?? new())
                {

                    var isExist = await CheckTopicExistsAsync(topicName);
                    if (isExist) continue;

                    var createTopicOptions = new CreateTopicOptions(topicName);
                    createTopicOptions.MaxSizeInMegabytes = _messageConfiguration?.AzureServiceBusConfiguration?.TopicMaxSizeInMegabytes ?? 1024;
                    createTopicOptions.DefaultMessageTimeToLive = _messageConfiguration?.AzureServiceBusConfiguration?.TopicDefaultMessageTimeToLive ?? TimeSpan.FromDays(30);

                    tasks.Add(_adminClient.CreateTopicAsync(createTopicOptions)); 
                }

                await Task.WhenAll(tasks);

                var subTasks = _messageConfiguration?.AzureServiceBusConfiguration?.Topics.Select(topicName => CreateTopicSubscriptionAsync(topicName));

                await Task.WhenAll(subTasks);

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
                createTopicSubscriptionOptions.MaxDeliveryCount = _messageConfiguration?.AzureServiceBusConfiguration?.TopicSubscriptionMaxDeliveryCount ?? 5;
                createTopicSubscriptionOptions.DefaultMessageTimeToLive = _messageConfiguration?.AzureServiceBusConfiguration?.TopicSubscriptionDefaultMessageTimeToLive ?? TimeSpan.FromDays(7);
                createTopicSubscriptionOptions.LockDuration = TimeSpan.FromHours(1);

                await _adminClient.CreateSubscriptionAsync(createTopicSubscriptionOptions); // don't await it need synchronization

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
