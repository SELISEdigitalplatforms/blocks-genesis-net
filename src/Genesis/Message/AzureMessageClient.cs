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

        public AzureMessageClient(ILogger<AzureMessageClient> logger, MessageConfiguration messageConfiguration)
        {
            _logger = logger;
            _messageConfiguration = messageConfiguration;
            _client = new ServiceBusClient(_messageConfiguration.Connection);
            _senders = new ConcurrentDictionary<string, ServiceBusSender>();

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
            
            var activity = Activity.Current;

            var sender = GetSender(consumerMessage.ConsumerName);
            var messageBody = new Message
            {
                Body = JsonConvert.SerializeObject(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };

            var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody));

            if (activity != null)
            {
                message.ApplicationProperties["TraceId"] = activity.TraceId.ToString();
                message.ApplicationProperties["SpanId"] = activity.SpanId.ToString();
                message.ApplicationProperties["ParentSpanId"] = activity.ParentSpanId.ToString();
            }

            await sender.SendMessageAsync(message);  
        }

        public async Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            var activity = Activity.Current;
            var sender = GetSender(consumerMessage.ConsumerName);

            var messageBody = new Message
            {
                Body = JsonConvert.SerializeObject(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };
            var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody));

            if (activity != null)
            {
                message.ApplicationProperties["TraceId"] = activity.TraceId.ToString();
                message.ApplicationProperties["SpanId"] = activity.SpanId.ToString();
                message.ApplicationProperties["ParentSpanId"] = activity.ParentSpanId.ToString();
            }

            await sender.SendMessageAsync(message);
        }
    }
}
