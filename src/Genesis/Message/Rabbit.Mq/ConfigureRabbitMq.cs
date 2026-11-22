using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

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
        var log = StartupRetry.LoggerOrDefault(logger);

        if (string.IsNullOrWhiteSpace(messageConfiguration.Connection) ||
            messageConfiguration.RabbitMqConfiguration == null)
        {
            return;
        }

        try
        {
            // The broker may still be starting: connect and declare under a bounded retry (Startup__DependencyWaitSeconds).
            await StartupRetry.ExecuteAsync("RabbitMQ", async cancellationToken =>
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(messageConfiguration.Connection),
                    //VirtualHost = "/",
                };

                await using var connection = await factory.CreateConnectionAsync(cancellationToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

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
            }, log, IsTransient);

            log.LogInformation("RabbitMQ topology (queues/exchanges/bindings) provisioned successfully.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to provision RabbitMQ topology.");
            throw;
        }
    }

    /// <summary>
    /// A failure worth waiting out (broker down or still starting, network). Not worth it: a malformed connection
    /// string, and the broker refusing the request itself (403 access refused, 404 not found, 406 precondition failed).
    /// </summary>
    internal static bool IsTransient(Exception exception) => exception switch
    {
        UriFormatException or ArgumentException or NotSupportedException or OperationCanceledException => false,
        OperationInterruptedException { ShutdownReason.ReplyCode: 403 or 404 or 406 } => false,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsTransient),
        _ => true
    };
}
