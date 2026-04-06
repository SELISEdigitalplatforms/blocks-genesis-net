using Blocks.Genesis;

namespace XUnitTest.Entities;

public class BaseModelsTests
{
    [Fact]
    public void BaseGetsRequest_ShouldExposeDefaults()
    {
        var request = new BaseGetsRequest<string>();

        Assert.Equal(0, request.Page);
        Assert.Equal(10, request.PageSize);
        Assert.Null(request.Sort);
        Assert.Null(request.Filter);
    }

    [Fact]
    public void BaseSortRequest_ShouldStoreAssignedValues()
    {
        var sort = new BaseSortRequest
        {
            Property = "CreatedAt",
            IsDescending = true
        };

        Assert.Equal("CreatedAt", sort.Property);
        Assert.True(sort.IsDescending);
    }

    [Fact]
    public void BaseResponseTypes_ShouldStoreAssignedValues()
    {
        var mutation = new BaseMutationResponse
        {
            IsSuccess = true,
            ItemId = "item-1",
            Errors = new Dictionary<string, string> { ["code"] = "none" }
        };

        var query = new BaseQueryResponse<int>
        {
            Data = 42,
            Errors = new Dictionary<string, string>()
        };

        var listQuery = new BaseQueryListResponse<List<int>>
        {
            Data = [1, 2],
            TotalCount = 2
        };

        Assert.True(mutation.IsSuccess);
        Assert.Equal("item-1", mutation.ItemId);
        Assert.Equal(42, query.Data);
        Assert.Equal(2, listQuery.TotalCount);
    }
}