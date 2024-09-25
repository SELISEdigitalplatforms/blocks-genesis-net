using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;

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

            foreach (var queue in messageConfiguration.Queues)
            {
                GetSender(queue);
            }

            foreach (var topic in messageConfiguration.Topics)
            {
                GetSender(topic);
            }
        }

        private ServiceBusSender GetSender(string consumerName)
        {
            return _senders.GetOrAdd(consumerName, name => _client.CreateSender(name));
        }

        public async Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            var currentActivity = Activity.Current;
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity("SendMessageToConsumer", ActivityKind.Producer, currentActivity?.Context ?? default);
   
            activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
            activity?.SetTag("consumer", JsonConvert.SerializeObject(consumerMessage));

            var sender = GetSender(consumerMessage.ConsumerName);
            var messageBody = new Message
            {
                Body = JsonConvert.SerializeObject(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };

            var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody))
            {
                ApplicationProperties =
                    {
                        ["TraceId"] = activity?.TraceId.ToString(),
                        ["SpanId"] = activity?.SpanId.ToString(), 
                        ["SecurityContext"] = JsonConvert.SerializeObject(securityContext)
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
