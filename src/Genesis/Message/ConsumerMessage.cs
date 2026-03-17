namespace Blocks.Genesis
{
    public record ConsumerMessage<T>
    {
        public required string ConsumerName { get; init; }
        public required T Payload { get; init; }
        public string Context {  get; init; }
        public DateTimeOffset? SccheduledEnqueueTimeUtc {  get; init; }
        public string RoutingKey { get; set; } = string.Empty;

    }
}
