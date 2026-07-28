using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Blocks.Genesis;

/// <summary>
/// Provisions RabbitMQ topology (queues, exchanges, bindings) eagerly at startup, independent
/// of whether this process also hosts a RabbitMessageWorker. Mirrors ConfigureAzureServiceBus,
/// which provisions Azure Service Bus queues/topics for any service that references them,
/// regardless of whether that service is a producer or a consumer.
/// </summary>
public static class ConfigureRabbitMq
{
    public static async Task ConfigureQueuesAndExchangesAsync(MessageConfiguration messageConfiguration, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(messageConfiguration.Connection) ||
            messageConfiguration.RabbitMqConfiguration == null)
        {
            return;
        }

        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(messageConfiguration.Connection),
                VirtualHost = "/",
            };

            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            foreach (var subscription in messageConfiguration.RabbitMqConfiguration.ConsumerSubscriptions)
            {
                await channel.QueueDeclareAsync(
                    queue: subscription.QueueName,
                    durable: subscription.Durable,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                if (!string.IsNullOrWhiteSpace(subscription.ExchangeName))
                {
                    await channel.ExchangeDeclareAsync(
                        exchange: subscription.ExchangeName,
                        type: subscription.ExchangeType,
                        durable: subscription.Durable,
                        autoDelete: false,
                        arguments: null);

                    await channel.QueueBindAsync(
                        queue: subscription.QueueName,
                        exchange: subscription.ExchangeName,
                        routingKey: subscription.RoutingKey,
                        arguments: null);
                }
            }

            log.LogInformation("RabbitMQ topology (queues/exchanges/bindings) provisioned successfully.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to provision RabbitMQ topology.");
            throw;
        }
    }
}
