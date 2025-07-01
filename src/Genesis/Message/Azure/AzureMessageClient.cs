using Azure.Messaging.ServiceBus;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public sealed class AzureMessageClient : IMessageClient
    {
        private readonly ServiceBusClient _client;
        private readonly ConcurrentDictionary<string, ServiceBusSender> _senders;
        private readonly ActivitySource _activitySource;

        public AzureMessageClient(
            MessageConfiguration messageConfiguration,
            ActivitySource activitySource)
        {
            _client = new ServiceBusClient(messageConfiguration.Connection);
            _senders = new ConcurrentDictionary<string, ServiceBusSender>();
            _activitySource = activitySource;

            InitializeSenders(messageConfiguration);
        }

        private void InitializeSenders(MessageConfiguration messageConfiguration)
        {
            foreach (var queue in messageConfiguration?.AzureServiceBusConfiguration?.Queues ?? [])
            {
                _senders.TryAdd(queue, _client.CreateSender(queue));
            }

            foreach (var topic in messageConfiguration?.AzureServiceBusConfiguration?.Topics ?? [])
            {
                _senders.TryAdd(topic, _client.CreateSender(topic));
            }
        }

        private ServiceBusSender GetSender(string consumerName)
        {
            return _senders.GetOrAdd(consumerName, name => _client.CreateSender(name));
        }

        private async Task SendToAzureBusAsync<T>(ConsumerMessage<T> consumerMessage, bool isTopic = false) where T : class
        {
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity(
                "messaging.azure.servicebus.send",
                ActivityKind.Producer,
                Activity.Current?.Context ?? default);

            if (activity != null)
            {
                activity.DisplayName = $"ServiceBus Send to {consumerMessage.ConsumerName}";
                activity.SetTag("messaging.system", "azure.servicebus");
                activity.SetTag("messaging.destination", consumerMessage.ConsumerName);
                activity.SetTag("messaging.destination_kind", isTopic ? "topic" : "queue");
                activity.SetTag("messaging.operation", "send");
                activity.SetTag("messaging.message_type", typeof(T).Name);
            }

            var sender = GetSender(consumerMessage.ConsumerName);

            var messageBody = new Message
            {
                Body = JsonSerializer.Serialize(consumerMessage.Payload),
                Type = typeof(T).Name
            };

            var message = new ServiceBusMessage(JsonSerializer.Serialize(messageBody))
            {
                ApplicationProperties =
                {
                    ["TenantId"] = securityContext?.TenantId,
                    ["TraceId"] = activity?.TraceId.ToString(),
                    ["SpanId"] = activity?.SpanId.ToString(),
                    ["SecurityContext"] = string.IsNullOrWhiteSpace(consumerMessage.Context)
                        ? JsonSerializer.Serialize(securityContext)
                        : consumerMessage.Context,
                    ["Baggage"] = JsonSerializer.Serialize(GetBaggageDictionary())
                }
            };

            await sender.SendMessageAsync(message);
        }

        private static Dictionary<string, string> GetBaggageDictionary()
        {
            var baggageDict = new Dictionary<string, string>();

            foreach (var item in Baggage.Current)
            {
                baggageDict[item.Key] = item.Value;
            }

            return baggageDict;
        }

        public async Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            await SendToAzureBusAsync(consumerMessage);
        }

        public async Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
        {
            await SendToAzureBusAsync(consumerMessage, true);
        }
    }
}
