using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Blocks.Genesis;

public class Consumer
{
    private readonly ILogger<Consumer> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly RoutingTable _routingTable;

    public Consumer(ILogger<Consumer> logger, IServiceProvider serviceProvider, RoutingTable routingTable)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _routingTable = routingTable;
    }

    public Task ProcessMessageAsync(string messageType, string messageBody)
        => ProcessMessageAsync(messageType, messageBody, null);

    /// <summary>
    /// Processes a message and exposes the queue it was consumed from as <see cref="MessageContext.Queue"/>
    /// to the consumer for the duration of the call.
    /// </summary>
    public async Task ProcessMessageAsync(string messageType, string messageBody, string? queueName)
    {
        try
        {
            MessageContext.SetQueue(queueName);

            // Get the type from the routing table
            if (!_routingTable.Routes.TryGetValue(messageType, out var routingInfo))
            {
                _logger.LogError("No consumer found for message type {MessageType}", messageType);
                return;
            }

            // Deserialize the message body into the specified type
            var deserializedBody = JsonSerializer.Deserialize(messageBody, routingInfo.ContextType);
            if (deserializedBody == null)
            {
                _logger.LogError("Failed to deserialize message body");
                return;
            }

            // Resolve the consumer
            var consumer = _serviceProvider.GetService(routingInfo.ConsumerType);
            if (consumer == null)
            {
                _logger.LogError("Failed to resolve consumer for message type {MessageType}", messageType);
                return;
            }

            // Invoke the consumer method
            await (Task)routingInfo.ConsumerMethod.Invoke(consumer, new object[] { deserializedBody })!;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
        finally
        {
            MessageContext.SetQueue(null);
        }
    }
}
