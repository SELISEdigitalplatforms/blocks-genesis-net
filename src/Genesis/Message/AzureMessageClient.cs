using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Diagnostics;

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
            var activity = Activity.Current;

            var sender = _client.CreateSender(consumerMessage.ConsumerName);
            var messageBody = new Message
            {
                Body = JsonConvert.SerializeObject(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name

            };
            var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody));

            message.ApplicationProperties["TraceId"] = activity?.TraceId.ToString();
            message.ApplicationProperties["SpanId"] = activity?.SpanId.ToString();
            message.ApplicationProperties["ParentSpanId"] = activity?.ParentSpanId.ToString(); 

            await sender.SendMessageAsync(message);
        }

        public async Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            //var activity = Activity.Current;
            //activity?.SetTag("message.type", consumerMessage.Payload.GetType().Name);
            //activity?.AddBaggage("traceparent", activity.Context.TraceState);

            //var sender = _client.CreateSender(consumerMessage.ConsumerName);
            //var messageBody = new Message
            //{
            //    Body = JsonConvert.SerializeObject(consumerMessage.Payload),
            //    Type = consumerMessage.Payload.GetType().Name
            //};
            //var message = new ServiceBusMessage(JsonConvert.SerializeObject(messageBody));
            
            //await sender.SendMessageAsync(message);
        }
    }
}
