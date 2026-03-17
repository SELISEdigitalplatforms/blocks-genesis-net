namespace Blocks.Genesis
{
    public class BaseResponse
    {
        public IDictionary<string, string>? Errors { get; set; }
        public bool IsSuccess { get; set; }
    }
}
