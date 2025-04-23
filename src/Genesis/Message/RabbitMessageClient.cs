using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

public sealed class RabbitMessageClient : IMessageClient, IAsyncDisposable
{
    private readonly ILogger<RabbitMessageClient> _logger;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly ActivitySource _activitySource;
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMessageClient(
        ILogger<RabbitMessageClient> logger,
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource)
    {
        _logger = logger;
        _messageConfiguration = messageConfiguration;
        _activitySource = activitySource;
    }

    public async Task InitializeAsync()
    {
        await CreateConnectionAsync();
        await InitializeChannelsAsync();
        AttachBasicReturnHandler();
    }

    private async Task CreateConnectionAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_messageConfiguration.Connection),
                VirtualHost = "/",
                ContinuationTimeout = new TimeSpan(0, 5, 2),
                AutomaticRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync();
            _logger.LogInformation("RabbitMQ connection established successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create RabbitMQ connection");
            throw;
        }
    }

    private async Task InitializeChannelsAsync()
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connection is not initialized");
        }

        try
        {
            _channel = await _connection.CreateChannelAsync();

            // Initialize queues
            foreach (var queue in _messageConfiguration.Queues)
            {
                await _channel.QueueDeclareAsync(
                    queue: queue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);
                _logger.LogDebug("Queue {QueueName} initialized", queue);
            }

            // Initialize exchanges and store them in the HashSet
            foreach (var exchange in _messageConfiguration.Topics)
            {
                await _channel.ExchangeDeclareAsync(
                    exchange: exchange,
                    type: ExchangeType.Fanout,
                    durable: true,
                    autoDelete: false,
                    arguments: null);
                _logger.LogDebug("Exchange {ExchangeName} initialized", exchange);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize channels");
            throw;
        }
    }

    private void AttachBasicReturnHandler()
    {
        if (_channel == null)
        {
            throw new InvalidOperationException("Channel is not initialized");
        }

        _channel.BasicReturnAsync += async (sender, ea) =>
        {
            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            _logger.LogWarning("Message returned: ReplyCode={ReplyCode}, ReplyText={ReplyText}, Exchange={Exchange}, RoutingKey={RoutingKey}, Body={Body}",
                ea.ReplyCode, ea.ReplyText, ea.Exchange, ea.RoutingKey, body);

            // Just to make the async lambda valid (the warning handler doesn't actually need to do anything asynchronous)
            await Task.CompletedTask;
        };
    }

    public async Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage, bool isExchange = false) where T : class
    {
        if (consumerMessage == null)
        {
            throw new ArgumentNullException(nameof(consumerMessage));
        }

        if (_channel == null)
        {
            await InitializeAsync();
            if (_channel == null)
            {
                throw new InvalidOperationException("Failed to initialize channel");
            }
        }

        var securityContext = BlocksContext.GetContext();

        using var activity = _activitySource.StartActivity("messaging.rabbitmq.send", ActivityKind.Producer, Activity.Current?.Context ?? default);

        activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", consumerMessage.ConsumerName);
        activity?.SetTag("messaging.destination.kind", isExchange ? "exchange" : "queue");
        activity?.SetTag("messaging.rabbitmq.routing_key", consumerMessage.RoutingKey ?? string.Empty);

        try
        {
            var messageBody = new Message
            {
                Body = JsonSerializer.Serialize(consumerMessage.Payload),
                Type = consumerMessage.Payload.GetType().Name
            };

            var messageJson = JsonSerializer.Serialize(messageBody);

            activity?.SetTag("messaging.message.body", messageJson);

            var body = Encoding.UTF8.GetBytes(messageJson);

            var properties = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object>
                {
                    ["TenantId"] = securityContext.TenantId,
                    ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty,
                    ["SpanId"] = activity?.SpanId.ToString() ?? string.Empty,
                    ["SecurityContext"] = string.IsNullOrWhiteSpace(consumerMessage.Context)
                        ? JsonSerializer.Serialize(securityContext)
                        : consumerMessage.Context
                }
            };

            // Optional: Set message expiration if configured
            if (_messageConfiguration.MessageTtlSeconds > 0)
            {
                properties.Expiration = (_messageConfiguration.MessageTtlSeconds * 1000).ToString();
            }

            if (isExchange)
            {
                await _channel.BasicPublishAsync(
                    exchange: consumerMessage.ConsumerName,
                    routingKey: consumerMessage.RoutingKey ?? string.Empty,
                    mandatory: true, // Ensure return if no queue is bound
                    basicProperties: properties,
                    body: body);
            }
            else
            {
                await _channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: consumerMessage.ConsumerName,
                    mandatory: true, // Ensure return if no queue is bound
                    basicProperties: properties,
                    body: body);
            }

            _logger.LogDebug("Message sent to {ConsumerName} with routing key {RoutingKey}",
                consumerMessage.ConsumerName, consumerMessage.RoutingKey ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {ConsumerName}", consumerMessage.ConsumerName);
            throw;
        }
    }

    public Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class
    {
        return SendToConsumerAsync(consumerMessage, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_channel?.IsOpen == true)
            {
                await _channel.CloseAsync();
                await _channel.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing channel");
        }

        if (_connection is not null)
        {
            try
            {
                if (_connection.IsOpen)
                {
                    await _connection.CloseAsync();
                    await _connection.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing connection");
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}