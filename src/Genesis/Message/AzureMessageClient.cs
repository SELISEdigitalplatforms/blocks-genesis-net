using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Blocks.Genesis
{
    public class AzureMessageClient : IMessageClient
    {
        private readonly ILogger<AzureMessageClient> _logger;
        private readonly MessageConfiguration _messageConfiguration;

        private ServiceBusClient _client;

        public AzureMessageClient(ILogger<AzureMessageClient> logger, MessageConfiguration messageConfiguration)
        {
            _logger = logger;
            _messageConfiguration = messageConfiguration;
            _client = new ServiceBusClient(_messageConfiguration.Connection);
        }

        public async Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            var sender = _client.CreateSender(consumerMessage.ConsumerName, new ServiceBusSenderOptions
            {
                Identifier = Guid.NewGuid().ToString()
            });
            var messageBody = new Message
            {
                Body = JsonConvert.SerializeObject(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };
            var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody));
            await sender.SendMessageAsync(message);
        }

        public async Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            var sender = _client.CreateSender(consumerMessage.ConsumerName, new ServiceBusSenderOptions
            {
                Identifier = Guid.NewGuid().ToString()
            });
            var messageBody = new Message
            {
                Body = JsonConvert.SerializeObject(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };
            var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody));
            await sender.SendMessageAsync(message);
        }
    }
}
