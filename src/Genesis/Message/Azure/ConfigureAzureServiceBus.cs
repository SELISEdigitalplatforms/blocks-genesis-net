using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocks.Genesis;

public static class ConfigureAzureServiceBus
{
    private static ServiceBusAdministrationClient _adminClient = null!;
    private static MessageConfiguration _messageConfiguration = null!;
    private static ILogger _logger = NullLogger.Instance;

    public static async Task ConfigureQueueAndTopicAsync(MessageConfiguration messageConfiguration, ILogger? logger = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(messageConfiguration.Connection))
            {
                ConfigureAzureServiceBusLog.MessageConnectionMissing(logger ?? _logger);
                return;
            }

            _adminClient = new ServiceBusAdministrationClient(messageConfiguration.Connection);
            _messageConfiguration = messageConfiguration;
            _logger = logger ?? _logger;

            var queueCreationTask = CreateQueuesAsync();
            var topicCreationTask = CreateTopicAsync();
            await Task.WhenAll(queueCreationTask, topicCreationTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConfigureAzureServiceBusLog.ConfigureEntitiesFailed(_logger, ex);
            throw;
        }
    }

    [Obsolete("Use ConfigureQueueAndTopicAsync instead.")]
    public static Task ConfigerQueueAndTopicAsync(MessageConfiguration messageConfiguration)
        => ConfigureQueueAndTopicAsync(messageConfiguration);

    private static async Task CreateQueuesAsync()
    {
        try
        {
            var tasks = new List<Task>();

            foreach (var queueName in _messageConfiguration?.AzureServiceBusConfiguration?.Queues ?? [])
            {
                var isExist = await CheckQueueExistsAsync(queueName).ConfigureAwait(false);
                if (isExist)
                {
                    continue;
                }

                var createQueueOptions = new CreateQueueOptions(queueName)
                {
                    MaxSizeInMegabytes = _messageConfiguration?.AzureServiceBusConfiguration?.QueueMaxSizeInMegabytes ?? 1024,
                    MaxDeliveryCount = _messageConfiguration?.AzureServiceBusConfiguration?.QueueMaxDeliveryCount ?? 2,
                    DefaultMessageTimeToLive = _messageConfiguration?.AzureServiceBusConfiguration?.QueueDefaultMessageTimeToLive ?? TimeSpan.FromDays(7),
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                tasks.Add(_adminClient.CreateQueueAsync(createQueueOptions));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConfigureAzureServiceBusLog.CreateQueuesFailed(_logger, ex);
            throw;
        }
    }

    private static async Task<bool> CheckQueueExistsAsync(string queue)
    {
        return await _adminClient.QueueExistsAsync(queue).ConfigureAwait(false);
    }

    private static async Task CreateTopicAsync()
    {
        try
        {
            var tasks = new List<Task>();

            foreach (var topicName in _messageConfiguration?.AzureServiceBusConfiguration?.Topics ?? [])
            {
                var isExist = await CheckTopicExistsAsync(topicName).ConfigureAwait(false);
                if (isExist)
                {
                    continue;
                }

                var createTopicOptions = new CreateTopicOptions(topicName)
                {
                    MaxSizeInMegabytes = _messageConfiguration?.AzureServiceBusConfiguration?.TopicMaxSizeInMegabytes ?? 1024,
                    DefaultMessageTimeToLive = _messageConfiguration?.AzureServiceBusConfiguration?.TopicDefaultMessageTimeToLive ?? TimeSpan.FromDays(30)
                };

                tasks.Add(_adminClient.CreateTopicAsync(createTopicOptions));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var subTasks = _messageConfiguration?.AzureServiceBusConfiguration?.Topics
                .Select(topicName => CreateTopicSubscriptionAsync(topicName));

            if (subTasks != null)
            {
                await Task.WhenAll(subTasks).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ConfigureAzureServiceBusLog.CreateTopicsFailed(_logger, ex);
            throw;
        }
    }

    private static async Task<bool> CheckTopicExistsAsync(string topicName)
    {
        return await _adminClient.TopicExistsAsync(topicName).ConfigureAwait(false);
    }

    private static async Task CreateTopicSubscriptionAsync(string topicName, string subscriptionName = "", string subscriptionFilter = "", string ruleOptionName = "BlocksRule")
    {
        try
        {
            subscriptionName = string.IsNullOrWhiteSpace(subscriptionName) ? _messageConfiguration.GetSubscriptionName(topicName) : subscriptionName;
            var isExist = await CheckSubscriptionExistsAsync(topicName, subscriptionName).ConfigureAwait(false);
            if (isExist)
            {
                return;
            }

            var createTopicSubscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = _messageConfiguration?.AzureServiceBusConfiguration?.TopicSubscriptionMaxDeliveryCount ?? 2,
                DefaultMessageTimeToLive = _messageConfiguration?.AzureServiceBusConfiguration?.TopicSubscriptionDefaultMessageTimeToLive ?? TimeSpan.FromDays(7),
                LockDuration = TimeSpan.FromMinutes(5)
            };

            if (!string.IsNullOrWhiteSpace(subscriptionFilter))
            {
                var correlationRule = new CreateRuleOptions(ruleOptionName, new CorrelationRuleFilter
                {
                    CorrelationId = subscriptionFilter
                });

                await _adminClient.CreateSubscriptionAsync(createTopicSubscriptionOptions, correlationRule).ConfigureAwait(false);
            }
            else
            {
                await _adminClient.CreateSubscriptionAsync(createTopicSubscriptionOptions).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ConfigureAzureServiceBusLog.CreateSubscriptionFailed(_logger, topicName, ex);
            throw;
        }
    }

    private static async Task<bool> CheckSubscriptionExistsAsync(string topicName, string subscriptionName)
    {
        return await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName).ConfigureAwait(false);
    }
}

internal static partial class ConfigureAzureServiceBusLog
{
    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Message connection is missing. Queue/topic provisioning skipped.")]
    public static partial void MessageConnectionMissing(ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Error, Message = "Failed to configure Azure Service Bus entities.")]
    public static partial void ConfigureEntitiesFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error, Message = "Failed creating Azure Service Bus queues.")]
    public static partial void CreateQueuesFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "Failed creating Azure Service Bus topics/subscriptions.")]
    public static partial void CreateTopicsFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Error, Message = "Failed creating subscription for topic {TopicName}.")]
    public static partial void CreateSubscriptionFailed(ILogger logger, string topicName, Exception exception);
}
