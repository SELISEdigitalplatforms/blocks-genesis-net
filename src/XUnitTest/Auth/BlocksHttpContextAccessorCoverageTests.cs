using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace XUnitTest.Auth;

// Serialized with the other coverage classes that swap the shared accessor instance.
[Collection("BlocksAuthStaticState")]
public class BlocksHttpContextAccessorCoverageTests
{
    [Fact]
    public void EnsureInitialized_ShouldThrow_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => BlocksHttpContextAccessor.EnsureInitialized(null!));
    }

    [Fact]
    public void EnsureInitialized_ShouldKeepExistingInstance()
    {
        var original = BlocksHttpContextAccessor.Instance;
        try
        {
            var sentinel = new HttpContextAccessor();
            BlocksHttpContextAccessor.Instance = sentinel;

            BlocksHttpContextAccessor.EnsureInitialized(new DefaultHttpContext());

            Assert.Same(sentinel, BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = original;
        }
    }

    [Fact]
    public void EnsureInitialized_ShouldStayNull_WhenRequestServicesAreMissing()
    {
        var original = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksHttpContextAccessor.Instance = null;

            BlocksHttpContextAccessor.EnsureInitialized(new DefaultHttpContext());

            Assert.Null(BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = original;
        }
    }

    [Fact]
    public void EnsureInitialized_ShouldResolveAccessor_FromRequestServices()
    {
        var original = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksHttpContextAccessor.Instance = null;

            var registered = new HttpContextAccessor();
            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(registered);
            var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

            BlocksHttpContextAccessor.EnsureInitialized(context);

            Assert.Same(registered, BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = original;
        }
    }

    [Fact]
    public void EnsureInitialized_ShouldCreateAccessor_WhenNoneIsRegistered()
    {
        var original = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksHttpContextAccessor.Instance = null;

            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

            BlocksHttpContextAccessor.EnsureInitialized(context);

            Assert.NotNull(BlocksHttpContextAccessor.Instance);
            Assert.IsType<HttpContextAccessor>(BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = original;
        }
    }
}
