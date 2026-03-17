
namespace Blocks.Genesis
{
    public class BaseQueryListResponse<T> : BaseQueryResponse<T>
    {
        public long TotalCount { get; set; }
    }
}
