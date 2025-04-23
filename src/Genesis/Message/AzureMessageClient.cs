using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class AzureMessageClient : IMessageClient
    {
        private readonly ILogger<AzureMessageClient> _logger;
        private readonly MessageConfiguration _messageConfiguration;
        private readonly ServiceBusClient _client;
        private readonly ConcurrentDictionary<string, ServiceBusSender> _senders;
        private readonly ActivitySource _activitySource;

        public AzureMessageClient(ILogger<AzureMessageClient> logger, MessageConfiguration messageConfiguration, ActivitySource activitySource)
        {
            _logger = logger;
            _messageConfiguration = messageConfiguration;
            _client = new ServiceBusClient(_messageConfiguration.Connection);
            _senders = new ConcurrentDictionary<string, ServiceBusSender>();
            _activitySource = activitySource;

            InitializeSenders(messageConfiguration);
        }

        private void InitializeSenders(MessageConfiguration messageConfiguration)
        {
            foreach (var queue in messageConfiguration.Queues)
            {
                _senders.TryAdd(queue, _client.CreateSender(queue));
            }

            foreach (var topic in messageConfiguration.Topics)
            {
                _senders.TryAdd(topic, _client.CreateSender(topic));
            }
        }

        private ServiceBusSender GetSender(string consumerName)
        {
            return _senders.GetOrAdd(consumerName, name => _client.CreateSender(name));
        }

        public async Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage, bool isExchange = false) where T : class
        {
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity("messaging.azure.service.bus.send", ActivityKind.Producer, Activity.Current?.Context ?? default);

            activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
            activity?.SetTag("consumer", JsonSerializer.Serialize(consumerMessage));

            var sender = GetSender(consumerMessage.ConsumerName);
            var messageBody = new Message
            {
                Body = JsonSerializer.Serialize(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };

            var message = new ServiceBusMessage(JsonSerializer.Serialize(messageBody))
            {
                ApplicationProperties =
                    {
                        ["TenantId"] = securityContext.TenantId,
                        ["TraceId"] = activity?.TraceId.ToString(),
                        ["SpanId"] = activity?.SpanId.ToString(),
                        ["SecurityContext"] = string.IsNullOrWhiteSpace(consumerMessage.Context) ? JsonSerializer.Serialize(securityContext) : consumerMessage.Context
                    }
            };

            // Send the message
            await sender.SendMessageAsync(message);

            // Stop activity
            activity?.Stop();
        }

        public async Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            await SendToConsumerAsync(consumerMessage);
        }
    }
}
