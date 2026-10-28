namespace Blocks.Genesis;

/// <summary>
/// The message Genesis sends to <see cref="BlocksEventTriggerConstants.QueueName"/> for an enabled catalog event.
/// The tenant travels in the <c>TenantId</c> bus property, not in the body.
/// </summary>
public sealed class BlocksEventTriggerMessage
{
    /// <summary>A new 32-hex-character ID per forward; the consumer's dedupe key.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>The original message's wire <c>Type</c>.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary><see cref="MessageConfiguration.ServiceName"/> of the producer, or empty.</summary>
    public string SourceService { get; set; } = string.Empty;

    /// <summary>The original queue, topic or exchange name.</summary>
    public string ConsumerName { get; set; } = string.Empty;

    /// <summary><c>true</c> when the original was sent with <c>SendToMassConsumerAsync</c>.</summary>
    public bool IsMassConsumer { get; set; }

    /// <summary>UTC time of the forward.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>The original <c>Message.Body</c>, exactly as sent.</summary>
    public string PayloadJson { get; set; } = string.Empty;
}
