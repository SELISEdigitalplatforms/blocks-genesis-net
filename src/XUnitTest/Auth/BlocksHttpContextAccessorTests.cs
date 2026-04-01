using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace XUnitTest.Auth;

public class BlocksHttpContextAccessorTests
{
    [Fact]
    public void Init_ShouldSetInstance()
    {
        var originalInstance = BlocksHttpContextAccessor.Instance;
        try
        {
            var httpContextAccessor = new HttpContextAccessor();
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
            var sp = services.BuildServiceProvider();

            BlocksHttpContextAccessor.Init(sp);

            Assert.NotNull(BlocksHttpContextAccessor.Instance);
            Assert.Same(httpContextAccessor, BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = originalInstance;
        }
    }

    [Fact]
    public void Instance_ShouldBeNullByDefault_WhenNotInitialized()
    {
        var original = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksHttpContextAccessor.Instance = null;
            Assert.Null(BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = original;
        }
    }
}
