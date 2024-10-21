namespace Blocks.Genesis
{
    public class BaseQueryResponse<T>
    {
        T? Data { get; set; }
        public IDictionary<string, string>? Errors { get; set; }
    }
}
