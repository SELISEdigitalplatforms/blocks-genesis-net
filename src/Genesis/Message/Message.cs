namespace Blocks.Genesis
{
    public record Message
    {
        public required string Body { get; set; }
        public required string Type { get; set; }
    }
}
