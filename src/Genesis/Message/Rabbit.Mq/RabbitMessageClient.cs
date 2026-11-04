using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace Blocks.Genesis;

public sealed class RabbitMessageClient : IMessageClient
{
    private const string UnknownValue = "unknown";

    private readonly ILogger<RabbitMessageClient> _logger;
    private readonly IRabbitMqService _rabbitMqService;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly ActivitySource _activitySource;
    private readonly IDelegationGrantFactory _delegationGrantFactory;

    private IChannel? _channel;
    private Task? _initializationTask;
    private bool _isInitialized;

    public RabbitMessageClient(
        ILogger<RabbitMessageClient> logger,
        IRabbitMqService rabbitMqService,
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource,
        IDelegationGrantFactory delegationGrantFactory)
    {
        _logger = logger;
        _rabbitMqService = rabbitMqService;
        _messageConfiguration = messageConfiguration;
        _activitySource = activitySource;
        _delegationGrantFactory = delegationGrantFactory;
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
            // Never the body: it is payload content.
            var wireType = ReadWireType(ea.Body.Span);
            var tenantId = ReadHeader(ea.BasicProperties?.Headers, "TenantId");
            _logger.LogWarning("Message returned: ReplyCode={ReplyCode}, ReplyText={ReplyText}, Exchange={Exchange}, RoutingKey={RoutingKey}, BodySize={BodySize}, Type={MessageType}, TenantId={TenantId}",
                ea.ReplyCode, ea.ReplyText, ea.Exchange, ea.RoutingKey, ea.Body.Length, wireType, tenantId);
            await Task.CompletedTask;
        };
    }

    // The string value of one top-level property, or null. Other values are skipped without being decoded.
    private static string? ReadTopLevelString(ReadOnlySpan<byte> json, string name)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var matches = reader.ValueTextEquals(name);
            reader.Read();
            if (matches && reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }

            reader.Skip();
        }

        return null;
    }

    /// <summary>The envelope's top-level <c>Type</c>, without decoding the body; <c>unknown</c> when unreadable.</summary>
    internal static string ReadWireType(ReadOnlySpan<byte> envelope)
    {
        try
        {
            return ReadTopLevelString(envelope, nameof(Message.Type)) ?? UnknownValue;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not a Genesis envelope (InvalidOperationException: a Type that is not valid UTF-8).
        }

        return UnknownValue;
    }

    private static string ReadHeader(IDictionary<string, object?>? headers, string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var value) || value is null)
        {
            return string.Empty;
        }

        return value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value.ToString() ?? string.Empty;
    }

    public Task SendMessageToConsumerAsync<T>(ConsumerMessage<T> consumerMessage, bool isExchange = false) where T : class
    {
        ArgumentNullException.ThrowIfNull(consumerMessage);

        return SendMessageToConsumerInternalAsync(consumerMessage, isExchange);
    }

    private async Task SendMessageToConsumerInternalAsync<T>(ConsumerMessage<T> consumerMessage, bool isExchange) where T : class
    {
        await EnsureInitializedAsync();

        var securityContext = BlocksContext.GetContext();

        using var activity = _activitySource.StartActivity("messaging.rabbitmq.send", ActivityKind.Producer, Activity.Current?.Context ?? default);

        if (activity is not null) activity.DisplayName = $"Rabbit Send to {consumerMessage.ConsumerName}";
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

            var exchange = isExchange ? consumerMessage.ConsumerName : string.Empty;
            var routingKey = isExchange ? (consumerMessage.RoutingKey ?? string.Empty) : consumerMessage.ConsumerName;

            await PublishEnvelopeAsync(
                exchange,
                routingKey,
                messageJson,
                securityContext,
                activity,
                consumerMessage.Context,
                consumerMessage.DelegationTtl);

            _logger.LogDebug("Message sent to {ConsumerName} with routing key {RoutingKey}",
                consumerMessage.ConsumerName, consumerMessage.RoutingKey ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {ConsumerName}", consumerMessage.ConsumerName);
        }
    }

    private async Task PublishEnvelopeAsync(
        string exchange,
        string routingKey,
        string envelopeJson,
        BlocksContext? securityContext,
        Activity? activity,
        string? providedContext,
        TimeSpan? delegationTtl)
    {
        var body = Encoding.UTF8.GetBytes(envelopeJson);

        var headers = new Dictionary<string, object>
        {
            ["TenantId"] = securityContext?.TenantId ?? string.Empty,
            ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty,
            ["SpanId"] = activity?.SpanId.ToString() ?? string.Empty,
            ["SecurityContext"] = BuildTransportContextJson(securityContext, providedContext),
            ["Baggage"] = JsonSerializer.Serialize(Activity.Current?.Baggage?.ToDictionary(b => b.Key, b => b.Value))
        };

        // Written while a validated user token is still in scope. SecurityContext above is context
        // and tracing only; this grant is what carries authority. No user, no grant, no header.
        var delegationGrant = await _delegationGrantFactory
            .CreateForSendAsync(delegationTtl)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(delegationGrant))
        {
            headers[DelegationConstants.DelegationGrantHeader] = delegationGrant;
        }

        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers
        };

        if (_messageConfiguration?.RabbitMqConfiguration?.MessageTtlSeconds > 0)
        {
            properties.Expiration = (_messageConfiguration.RabbitMqConfiguration.MessageTtlSeconds * 1000).ToString();
        }

        await _channel!.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: body);
    }

    public Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class =>
        SendMessageToConsumerAsync(consumerMessage, isExchange: false);

    public Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class =>
        SendMessageToConsumerAsync(consumerMessage, isExchange: true);

    private static string BuildTransportContextJson(BlocksContext? currentContext, string? providedContext)
    {
        if (!string.IsNullOrWhiteSpace(providedContext))
        {
            return providedContext;
        }

        return JsonSerializer.Serialize(BlocksContext.CreateSanitizedForTransport(currentContext));
    }
}
