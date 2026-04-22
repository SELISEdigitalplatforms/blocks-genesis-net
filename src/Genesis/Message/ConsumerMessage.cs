namespace Blocks.Genesis;

public record ConsumerMessage<T>
{
    public required string ConsumerName { get; init; }
    public required T Payload { get; init; }
    public string Context { get; init; } = string.Empty;
    public DateTimeOffset? ScheduledEnqueueTimeUtc { get; init; }

    [Obsolete("Use ScheduledEnqueueTimeUtc instead.")]
    public DateTimeOffset? SccheduledEnqueueTimeUtc
    {
        get => ScheduledEnqueueTimeUtc;
        init => ScheduledEnqueueTimeUtc = value;
    }

    public string RoutingKey { get; set; } = string.Empty;
}
