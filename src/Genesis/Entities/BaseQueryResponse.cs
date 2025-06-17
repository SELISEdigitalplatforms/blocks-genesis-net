namespace Blocks.Genesis
{
    public class BaseQueryResponse<T>
    {
        public T? Data { get; set; }
        public IDictionary<string, string>? Errors { get; set; }
    }
}
