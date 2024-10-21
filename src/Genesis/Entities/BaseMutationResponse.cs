namespace Blocks.Genesis
{
    public class BaseMutationResponse
    {
        public IDictionary<string, string>? Errors { get; set; }
        public bool IsSuccess { get; set; }
        public string? ItemId { get; set; }
    }
}
