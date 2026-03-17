namespace Blocks.Genesis
{
    public class BaseGetsRequest<T>
    {
        public int Page { get; set; } = 0;
        public int PageSize { get; set; } = 10;
        public BaseSortRequest? Sort { get; set; }
        public T? Filter { get; set; }
    }

    public class BaseSortRequest
    {
        public string Property { get; set; }
        public bool IsDescending { get; set; }
    }
}
