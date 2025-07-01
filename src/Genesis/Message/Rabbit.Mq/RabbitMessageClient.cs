using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

public sealed class RabbitMessageClient : IMessageClient
{
    private readonly ILogger<RabbitMessageClient> _logger;
    private readonly IRabbitMqService _rabbitMqService;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly ActivitySource _activitySource;

    private IChannel? _channel;
    private Task? _initializationTask;
    private bool _isInitialized;

    public RabbitMessageClient(
        ILogger<RabbitMessageClient> logger,
        IRabbitMqService rabbitMqService,
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource)
    {
        _logger = logger;
        _rabbitMqService = rabbitMqService;
        _messageConfiguration = messageConfiguration;
        _activitySource = activitySource;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized || _channel?.IsOpen == true)
            return;

        if (_initializationTask == null)
        {
            _initializationTask = InitializeRabbitMqAsync();
        }

        await _initializationTask;

        if (_channel?.IsOpen == true)
        {
            _isInitialized = true;
        }
        else
        {
            _logger.LogError("RabbitMQ channel is not open after initialization.");
            throw new InvalidOperationException("RabbitMQ channel is not open.");
        }
    }


    private async Task InitializeRabbitMqAsync()
    {
        await _rabbitMqService.CreateConnectionAsync();
        _channel = _rabbitMqService.RabbitMqChannel;

        if (_channel == null)
        {
            throw new InvalidOperationException("Channel is not initialized");
        }

        _channel.BasicReturnAsync += async (sender, ea) =>
        {
            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            _logger.LogWarning("Message returned: ReplyCode={ReplyCode}, ReplyText={ReplyText}, Exchange={Exchange}, RoutingKey={RoutingKey}, Body={Body}",
                ea.ReplyCode, ea.ReplyText, ea.Exchange, ea.RoutingKey, body);
            await Task.CompletedTask;
        };
    }

    public async Task SendMessageToConsumerAsync<T>(ConsumerMessage<T> consumerMessage, bool isExchange = false) where T : class
    {
        ArgumentNullException.ThrowIfNull(consumerMessage);

        await EnsureInitializedAsync();

        var securityContext = BlocksContext.GetContext();

        using var activity = _activitySource.StartActivity("messaging.rabbitmq.send", ActivityKind.Producer, Activity.Current?.Context ?? default);

        activity.DisplayName = $"Rabbit Send to {consumerMessage.ConsumerName}";
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
            var body = Encoding.UTF8.GetBytes(messageJson);

            activity?.SetTag("messaging.message.body", messageJson);

            var properties = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object>
                {
                    ["TenantId"] = securityContext?.TenantId ?? string.Empty,
                    ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty,
                    ["SpanId"] = activity?.SpanId.ToString() ?? string.Empty,
                    ["SecurityContext"] = string.IsNullOrWhiteSpace(consumerMessage.Context)
                        ? JsonSerializer.Serialize(securityContext)
                        : consumerMessage.Context,
                    ["Baggage"] = JsonSerializer.Serialize(Activity.Current?.Baggage?.ToDictionary(b => b.Key, b => b.Value))
                }
            };

            if (_messageConfiguration?.RabbitMqConfiguration?.MessageTtlSeconds > 0)
            {
                properties.Expiration = (_messageConfiguration.RabbitMqConfiguration.MessageTtlSeconds * 1000).ToString();
            }

            var exchange = isExchange ? consumerMessage.ConsumerName : string.Empty;
            var routingKey = isExchange ? (consumerMessage.RoutingKey ?? string.Empty) : consumerMessage.ConsumerName;

            await _channel!.BasicPublishAsync(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: properties,
                body: body);

            _logger.LogDebug("Message sent to {ConsumerName} with routing key {RoutingKey}",
                consumerMessage.ConsumerName, consumerMessage.RoutingKey ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {ConsumerName}", consumerMessage.ConsumerName);
        }
    }

    public Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class =>
        SendMessageToConsumerAsync(consumerMessage, isExchange: false);

    public Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class =>
        SendMessageToConsumerAsync(consumerMessage, isExchange: true);
}
