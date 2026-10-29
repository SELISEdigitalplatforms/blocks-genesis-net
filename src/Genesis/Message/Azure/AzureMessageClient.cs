using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis;

public sealed class AzureMessageClient : IMessageClient
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders;
    private readonly ActivitySource _activitySource;
    private readonly IDelegationGrantFactory _delegationGrantFactory;
    private readonly IBlocksEventTriggerForwarder? _forwarder;
    private readonly Func<ConsumerMessage<BlocksEventTriggerMessage>, string, Task> _sendForward;
    private readonly ILogger<AzureMessageClient> _logger;

    // Forwards go through their own client, created on the first forward, whose SDK retry policy makes a single
    // attempt: the forward is best-effort and must not wait through a retry ladder on the send path. Originals
    // keep _client and its default retries. The cost is one more AMQP connection in a process that forwards.
    private readonly Lazy<ServiceBusClient> _forwardClient;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _forwardSenders = new();

    /// <summary>Creates a client that does not forward Blocks Event Trigger events.</summary>
    public AzureMessageClient(
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource,
        IDelegationGrantFactory delegationGrantFactory)
        : this(messageConfiguration, activitySource, delegationGrantFactory, null)
    {
    }

    /// <summary>
    /// Creates a client that, after each completed send, lets <paramref name="blocksEventTriggerForwarder"/>
    /// forward enabled catalog events. Dependency injection uses this constructor.
    /// </summary>
    /// <param name="logger">Receives a warning if the forwarder throws; the original send still succeeds.</param>
    public AzureMessageClient(
        MessageConfiguration messageConfiguration,
        ActivitySource activitySource,
        IDelegationGrantFactory delegationGrantFactory,
        IBlocksEventTriggerForwarder? blocksEventTriggerForwarder,
        ILogger<AzureMessageClient>? logger = null)
    {
        _client = new ServiceBusClient(messageConfiguration.Connection);
        _senders = new ConcurrentDictionary<string, ServiceBusSender>();
        _activitySource = activitySource;
        _delegationGrantFactory = delegationGrantFactory;
        _forwarder = blocksEventTriggerForwarder;
        _sendForward = SendForwardAsync;
        _logger = logger ?? NullLogger<AzureMessageClient>.Instance;

        var connection = messageConfiguration.Connection;
        _forwardClient = new Lazy<ServiceBusClient>(() => new ServiceBusClient(connection, CreateForwardClientOptions()));

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

    /// <summary>Options of the forwards' client: one attempt, no SDK-level retries.</summary>
    internal static ServiceBusClientOptions CreateForwardClientOptions() => new()
    {
        RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0 }
    };

    private ServiceBusSender GetForwardSender(string consumerName)
    {
        return _forwardSenders.GetOrAdd(consumerName, name => _forwardClient.Value.CreateSender(name));
    }

    private async Task SendToAzureBusAsync<T>(ConsumerMessage<T> consumerMessage, bool isTopic = false) where T : class
    {
        var securityContext = BlocksContext.GetContext();

        using var activity = StartSendActivity(consumerMessage.ConsumerName, isTopic, typeof(T).Name);

        var sender = GetSender(consumerMessage.ConsumerName);

        var messageBody = new Message
        {
            Body = JsonSerializer.Serialize(consumerMessage.Payload),
            Type = typeof(T).Name
        };

        await SendEnvelopeAsync(
            sender,
            JsonSerializer.Serialize(messageBody),
            securityContext,
            activity,
            consumerMessage.Context,
            consumerMessage.DelegationTtl,
            consumerMessage.ScheduledEnqueueTimeUtc);

        // Reached only when the original send or schedule completed.
        if (_forwarder is not null)
        {
            try
            {
                await _forwarder.TryForwardAsync(
                    consumerMessage,
                    messageBody.Type,
                    messageBody.Body,
                    securityContext?.TenantId,
                    isTopic,
                    activity,
                    _sendForward);
            }
            catch (Exception ex)
            {
                // The forwarder is a public extension point. If one throws anyway, the original was still
                // delivered, so the caller must not see a failure (and resend it). Type only: no message content.
                _logger.LogWarning( // NOSONAR S6667 — type only: the exception can quote message content
                    "Blocks event trigger forwarder threw after a completed send to {ConsumerName} ({ErrorType})",
                    consumerMessage.ConsumerName, ex.GetType().Name);
            }
        }
    }

    // The forward's own send. Unlike an original it is not forwarded again, and its failures reach the forwarder.
    private async Task SendForwardAsync(ConsumerMessage<BlocksEventTriggerMessage> forward, string envelopeJson)
    {
        var securityContext = BlocksContext.GetContext();

        using var activity = StartSendActivity(forward.ConsumerName, isTopic: false, nameof(BlocksEventTriggerMessage));

        await SendEnvelopeAsync(
            GetForwardSender(forward.ConsumerName),
            envelopeJson,
            securityContext,
            activity,
            forward.Context,
            forward.DelegationTtl,
            forward.ScheduledEnqueueTimeUtc);
    }

    private Activity? StartSendActivity(string consumerName, bool isTopic, string messageType)
    {
        var activity = _activitySource.StartActivity(
            "messaging.azure.servicebus.send",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            activity.DisplayName = $"ServiceBus Send to {consumerName}";
            activity.SetTag("messaging.system", "azure.servicebus");
            activity.SetTag("messaging.destination", consumerName);
            activity.SetTag("messaging.destination_kind", isTopic ? "topic" : "queue");
            activity.SetTag("messaging.operation", "send");
            activity.SetTag("messaging.message_type", messageType);
        }

        return activity;
    }

    private async Task SendEnvelopeAsync(
        ServiceBusSender sender,
        string envelopeJson,
        BlocksContext? securityContext,
        Activity? activity,
        string? providedContext,
        TimeSpan? delegationTtl,
        DateTimeOffset? scheduledEnqueueTimeUtc)
    {
        var message = new ServiceBusMessage(envelopeJson)
        {
            ApplicationProperties =
            {
                ["TenantId"] = securityContext?.TenantId,
                ["TraceId"] = activity?.TraceId.ToString(),
                ["SpanId"] = activity?.SpanId.ToString(),
                ["SecurityContext"] = BuildTransportContextJson(securityContext, providedContext),
                ["Baggage"] = JsonSerializer.Serialize(GetBaggageDictionary())
            }
        };

        // Written while a validated user token is still in scope. SecurityContext above is context
        // and tracing only; this grant is what carries authority. No user, no grant, no header.
        var delegationGrant = await _delegationGrantFactory
            .CreateForSendAsync(delegationTtl)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(delegationGrant))
        {
            message.ApplicationProperties[DelegationConstants.DelegationGrantHeader] = delegationGrant;
        }
        if (scheduledEnqueueTimeUtc is not null)
        {
            await sender.ScheduleMessageAsync(message, scheduledEnqueueTimeUtc.Value);
        }
        else
        {
            await sender.SendMessageAsync(message);
        }
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

    private static string BuildTransportContextJson(BlocksContext? currentContext, string? providedContext)
    {
        if (!string.IsNullOrWhiteSpace(providedContext))
        {
            return providedContext;
        }

        return JsonSerializer.Serialize(BlocksContext.CreateSanitizedForTransport(currentContext));
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
