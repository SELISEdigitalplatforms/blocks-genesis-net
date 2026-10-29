using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace Blocks.Genesis;

public sealed class RabbitMessageClient : IMessageClient
{
    private readonly ILogger<RabbitMessageClient> _logger;
    private readonly IRabbitMqService _rabbitMqService;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly ActivitySource _activitySource;
    private readonly IDelegationGrantFactory _delegationGrantFactory;
    private readonly IBlocksEventTriggerForwarder? _forwarder;
    private readonly Func<ConsumerMessage<BlocksEventTriggerMessage>, string, Task> _sendForward;

    private IChannel? _channel;
    private Task? _initializationTask;
    private bool _isInitialized;

    /// <summary>Creates a client that does not forward Blocks Event Trigger events.</summary>
    public RabbitMessageClient(
        ILogger<RabbitMessageClient> logger,
        IRabbitMqService rabbitMqService,
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource,
        IDelegationGrantFactory delegationGrantFactory)
        : this(logger, rabbitMqService, messageConfiguration, activitySource, delegationGrantFactory, null)
    {
    }

    /// <summary>
    /// Creates a client that, after each completed publish, lets <paramref name="blocksEventTriggerForwarder"/>
    /// forward enabled catalog events. Dependency injection uses this constructor.
    /// </summary>
    public RabbitMessageClient(
        ILogger<RabbitMessageClient> logger,
        IRabbitMqService rabbitMqService,
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource,
        IDelegationGrantFactory delegationGrantFactory,
        IBlocksEventTriggerForwarder? blocksEventTriggerForwarder)
    {
        _logger = logger;
        _rabbitMqService = rabbitMqService;
        _messageConfiguration = messageConfiguration;
        _activitySource = activitySource;
        _delegationGrantFactory = delegationGrantFactory;
        _forwarder = blocksEventTriggerForwarder;
        _sendForward = PublishForwardAsync;
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
            if (wireType == nameof(BlocksEventTriggerMessage))
            {
                // A returned forward (C6): the tenant and the forwarded event's name, still without the payload.
                _logger.LogWarning("Message returned: ReplyCode={ReplyCode}, ReplyText={ReplyText}, Exchange={Exchange}, RoutingKey={RoutingKey}, BodySize={BodySize}, Type={MessageType}, TenantId={TenantId}, EventName={EventName}",
                    ea.ReplyCode, ea.ReplyText, ea.Exchange, ea.RoutingKey, ea.Body.Length, wireType, tenantId, ReadForwardEventName(ea.Body.Span));
            }
            else
            {
                _logger.LogWarning("Message returned: ReplyCode={ReplyCode}, ReplyText={ReplyText}, Exchange={Exchange}, RoutingKey={RoutingKey}, BodySize={BodySize}, Type={MessageType}, TenantId={TenantId}",
                    ea.ReplyCode, ea.ReplyText, ea.Exchange, ea.RoutingKey, ea.Body.Length, wireType, tenantId);
            }
            await Task.CompletedTask;
        };
    }

    /// <summary>
    /// The <c>EventName</c> of a forward envelope (<c>Body</c> is a serialized <see cref="BlocksEventTriggerMessage"/>);
    /// <c>unknown</c> when unreadable. Reads only that field; <c>PayloadJson</c> is never returned.
    /// </summary>
    internal static string ReadForwardEventName(ReadOnlySpan<byte> envelope)
    {
        try
        {
            var body = ReadTopLevelString(envelope, nameof(Message.Body));
            if (body is not null)
            {
                return ReadTopLevelString(Encoding.UTF8.GetBytes(body), nameof(BlocksEventTriggerMessage.EventName)) ?? "unknown";
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not a forward envelope.
        }

        return "unknown";
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
            return ReadTopLevelString(envelope, nameof(Message.Type)) ?? "unknown";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not a Genesis envelope (InvalidOperationException: a Type that is not valid UTF-8).
        }

        return "unknown";
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

            // Reached only when the original publish completed.
            if (_forwarder is not null)
            {
                try
                {
                    await _forwarder.TryForwardAsync(
                        consumerMessage,
                        messageBody.Type,
                        messageBody.Body,
                        securityContext?.TenantId,
                        isExchange,
                        activity,
                        _sendForward);
                }
                catch (Exception ex)
                {
                    // The forwarder is a public extension point. If one throws anyway, the original was still
                    // published: not a "Failed to send message" error. Type only: no message content.
                    _logger.LogWarning(
                        "Blocks event trigger forwarder threw after a completed send to {ConsumerName} ({ErrorType})",
                        consumerMessage.ConsumerName, ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {ConsumerName}", consumerMessage.ConsumerName);
        }
    }

    // The forward's own publish. Unlike an original it is not forwarded again, and its failures are not swallowed
    // here: they reach the forwarder, which logs them as forward failures. RabbitMQ has no scheduling, so
    // ScheduledEnqueueTimeUtc is ignored, as it is for originals.
    private async Task PublishForwardAsync(ConsumerMessage<BlocksEventTriggerMessage> forward, string envelopeJson)
    {
        var securityContext = BlocksContext.GetContext();

        using var activity = _activitySource.StartActivity("messaging.rabbitmq.send", ActivityKind.Producer, Activity.Current?.Context ?? default);

        if (activity is not null) activity.DisplayName = $"Rabbit Send to {forward.ConsumerName}";
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", forward.ConsumerName);
        activity?.SetTag("messaging.destination.kind", "queue");
        activity?.SetTag("messaging.rabbitmq.routing_key", forward.ConsumerName);

        await PublishEnvelopeAsync(
            string.Empty,
            forward.ConsumerName,
            envelopeJson,
            securityContext,
            activity,
            forward.Context,
            forward.DelegationTtl);
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
