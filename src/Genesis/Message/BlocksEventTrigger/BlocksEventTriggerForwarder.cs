using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

/// <summary>
/// Sends one <see cref="BlocksEventTriggerMessage"/> to <see cref="BlocksEventTriggerConstants.QueueName"/> after an
/// original send, when the sending tenant enabled the original's wire type for workflows. Best-effort: it logs
/// and swallows every failure, never retries, and never logs or tags payload content.
/// </summary>
internal sealed class BlocksEventTriggerForwarder : IBlocksEventTriggerForwarder
{
    internal const string ForwardedTag = "blocks.event_trigger.forwarded";
    internal const string SkipReasonTag = "blocks.event_trigger.skip_reason";

    internal const string SkipRecursion = "recursion";
    internal const string SkipNoTenant = "no_tenant";
    internal const string SkipNotEnabled = "not_enabled";
    internal const string SkipTooLarge = "too_large";
    internal const string SkipFailed = "failed";

    private static readonly object True = true;
    private static readonly object False = false;

    private readonly IBlocksEventCatalogCache _catalogCache;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BlocksEventTriggerForwarder> _logger;

    public BlocksEventTriggerForwarder(
        IBlocksEventCatalogCache catalogCache,
        MessageConfiguration messageConfiguration,
        TimeProvider timeProvider,
        ILogger<BlocksEventTriggerForwarder> logger)
    {
        _catalogCache = catalogCache;
        _messageConfiguration = messageConfiguration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask TryForwardAsync<T>(
        ConsumerMessage<T> original,
        string wireType,
        string payloadJson,
        string? tenantId,
        bool isMassConsumer,
        Activity? producerActivity,
        Func<ConsumerMessage<BlocksEventTriggerMessage>, string, Task> sendForward) where T : class
    {
        // Wire type, not only T: RabbitMQ puts the payload's runtime type on the wire.
        if (wireType == nameof(BlocksEventTriggerMessage) || typeof(T) == typeof(BlocksEventTriggerMessage))
        {
            Skip(producerActivity, SkipRecursion);
            return;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            Skip(producerActivity, SkipNoTenant);
            return;
        }

        try
        {
            var lookup = _catalogCache.GetEnabledEventsAsync(tenantId);
            var enabled = lookup.IsCompletedSuccessfully ? lookup.Result : await lookup.ConfigureAwait(false);

            if (!enabled.Contains(wireType))
            {
                Skip(producerActivity, SkipNotEnabled);
                return;
            }

            var trigger = new BlocksEventTriggerMessage
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventName = wireType,
                SourceService = _messageConfiguration.ServiceName ?? string.Empty,
                ConsumerName = original.ConsumerName,
                IsMassConsumer = isMassConsumer,
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                PayloadJson = payloadJson
            };

            var envelopeJson = JsonSerializer.Serialize(new Message
            {
                Body = JsonSerializer.Serialize(trigger),
                Type = nameof(BlocksEventTriggerMessage)
            });

            var size = Encoding.UTF8.GetByteCount(envelopeJson);
            if (size > BlocksEventTriggerConstants.MaxForwardBytes)
            {
                _logger.LogWarning(
                    "Blocks event trigger forward dropped for tenant {TenantId}, event {EventName}: size={Size} bytes exceeds {MaxBytes}",
                    tenantId, wireType, size, BlocksEventTriggerConstants.MaxForwardBytes);
                Skip(producerActivity, SkipTooLarge);
                return;
            }

            await sendForward(new ConsumerMessage<BlocksEventTriggerMessage>
            {
                ConsumerName = BlocksEventTriggerConstants.QueueName,
                Payload = trigger,
                Context = original.Context,
                ScheduledEnqueueTimeUtc = original.ScheduledEnqueueTimeUtc,
                DelegationTtl = original.DelegationTtl
            }, envelopeJson).ConfigureAwait(false);

            producerActivity?.SetTag(ForwardedTag, True);
        }
        catch (Exception ex)
        {
            // The exception message is left out: a serializer or transport error can quote message content.
            _logger.LogWarning(
                "Blocks event trigger forward failed for tenant {TenantId}, event {EventName} ({ErrorType})",
                tenantId, wireType, ex.GetType().Name);
            Skip(producerActivity, SkipFailed);
        }
    }

    private static void Skip(Activity? activity, string reason)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ForwardedTag, False);
        activity.SetTag(SkipReasonTag, reason);
    }
}
