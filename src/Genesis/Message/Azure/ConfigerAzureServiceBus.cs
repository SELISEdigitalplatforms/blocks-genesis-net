using Azure.Messaging.ServiceBus.Administration;

namespace Blocks.Genesis
{
    public static class ConfigerAzureServiceBus
    {
        public static async Task ConfigerQueueAndTopicAsync(MessageConfiguration messageConfiguration)
        {
            ArgumentNullException.ThrowIfNull(messageConfiguration);

            try
            {
                if (string.IsNullOrWhiteSpace(messageConfiguration.Connection))
                {
                    throw new InvalidOperationException("Message connection string is required for Azure Service Bus provisioning.");
                }

                var adminClient = new ServiceBusAdministrationClient(messageConfiguration.Connection);

                var queueCreationTask = CreateQueuesAsync(adminClient, messageConfiguration);
                var topicCreationTask = CreateTopicAsync(adminClient, messageConfiguration);
                await Task.WhenAll(queueCreationTask, topicCreationTask);
            }
            catch
            {
                throw;
            }
        }

        private static async Task CreateQueuesAsync(ServiceBusAdministrationClient adminClient, MessageConfiguration messageConfiguration)
        {
            var tasks = new List<Task>();

            foreach (var queueName in messageConfiguration?.AzureServiceBusConfiguration?.Queues ?? new())
            {
                var isExist = await CheckQueueExistsAsync(adminClient, queueName);
                if (isExist) continue;

                var createQueueOptions = new CreateQueueOptions(queueName)
                {
                    MaxSizeInMegabytes = messageConfiguration?.AzureServiceBusConfiguration?.QueueMaxSizeInMegabytes ?? 1024,
                    MaxDeliveryCount = messageConfiguration?.AzureServiceBusConfiguration?.QueueMaxDeliveryCount ?? 2,
                    DefaultMessageTimeToLive = messageConfiguration?.AzureServiceBusConfiguration?.QueueDefaultMessageTimeToLive ?? TimeSpan.FromDays(7),
                    RequiresSession = messageConfiguration?.AzureServiceBusConfiguration?.EnableSessions ?? false,
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                tasks.Add(adminClient.CreateQueueAsync(createQueueOptions));
            }

            await Task.WhenAll(tasks);
        }

        private static async Task<bool> CheckQueueExistsAsync(ServiceBusAdministrationClient adminClient, string queue)
        {
            return await adminClient.QueueExistsAsync(queue);
        }

        private static async Task CreateTopicAsync(ServiceBusAdministrationClient adminClient, MessageConfiguration messageConfiguration)
        {
            var tasks = new List<Task>();

            foreach (var topicName in messageConfiguration?.AzureServiceBusConfiguration?.Topics ?? [])
            {
                var isExist = await CheckTopicExistsAsync(adminClient, topicName);
                if (isExist) continue;

                var createTopicOptions = new CreateTopicOptions(topicName)
                {
                    MaxSizeInMegabytes = messageConfiguration?.AzureServiceBusConfiguration?.TopicMaxSizeInMegabytes ?? 1024,
                    DefaultMessageTimeToLive = messageConfiguration?.AzureServiceBusConfiguration?.TopicDefaultMessageTimeToLive ?? TimeSpan.FromDays(30)
                };

                tasks.Add(adminClient.CreateTopicAsync(createTopicOptions));
            }

            await Task.WhenAll(tasks);

            var subTasks = (messageConfiguration?.AzureServiceBusConfiguration?.Topics ?? [])
                .Select(topicName => CreateTopicSubscriptionAsync(adminClient, messageConfiguration, topicName));

            await Task.WhenAll(subTasks);
        }

        private static async Task<bool> CheckTopicExistsAsync(ServiceBusAdministrationClient adminClient, string topicName)
        {
            return await adminClient.TopicExistsAsync(topicName);
        }

        private static async Task CreateTopicSubscriptionAsync(
            ServiceBusAdministrationClient adminClient,
            MessageConfiguration messageConfiguration,
            string topicName,
            string subscriptionName = "",
            string subscriptionFilter = "",
            string ruleOptionName = "BlocksRule")
        {
            subscriptionName = string.IsNullOrWhiteSpace(subscriptionName) ? messageConfiguration.GetSubscriptionName(topicName) : subscriptionName;
            var isExist = await CheckSubscriptionExistsAsync(adminClient, topicName, subscriptionName);
            if (isExist) return;

            var createTopicSubscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = messageConfiguration?.AzureServiceBusConfiguration?.TopicSubscriptionMaxDeliveryCount ?? 2,
                DefaultMessageTimeToLive = messageConfiguration?.AzureServiceBusConfiguration?.TopicSubscriptionDefaultMessageTimeToLive ?? TimeSpan.FromDays(7),
                RequiresSession = messageConfiguration?.AzureServiceBusConfiguration?.EnableSessions ?? false,
                LockDuration = TimeSpan.FromMinutes(5)
            };

            if (!string.IsNullOrWhiteSpace(subscriptionFilter))
            {
                var correlationRule = new CreateRuleOptions(ruleOptionName, new CorrelationRuleFilter
                {
                    CorrelationId = subscriptionFilter
                });

                await adminClient.CreateSubscriptionAsync(createTopicSubscriptionOptions, correlationRule);
            }
            else
            {
                await adminClient.CreateSubscriptionAsync(createTopicSubscriptionOptions);
            }
        }

        private static async Task<bool> CheckSubscriptionExistsAsync(ServiceBusAdministrationClient adminClient, string topicName, string subscriptionName)
        {
            return await adminClient.SubscriptionExistsAsync(topicName, subscriptionName);
        }
    }
}
