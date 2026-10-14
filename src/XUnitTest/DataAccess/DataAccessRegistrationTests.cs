using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace XUnitTest.DataAccess;

public class DataAccessRegistrationTests
{
    private sealed class HostAccessor : IAccessPrincipalAccessor
    {
        public AccessPrincipal Current => AccessPrincipal.Anonymous with { IsRootTenant = true };
    }

    private static ServiceProvider Build(Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IDbContextProvider>());
        before?.Invoke(services);
        services.AddBlocksDataAccess();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddBlocksDataAccess_RegistersBothResolvers_AndANeverRootDefaultCaller()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<IDataAccessResolver>());
        Assert.NotNull(provider.GetRequiredService<IDataAccessPolicyResolver>());
        var accessor = provider.GetRequiredService<IAccessPrincipalAccessor>();
        Assert.IsType<BlocksContextAccessPrincipalAccessor>(accessor);
        Assert.False(accessor.Current.IsRootTenant);
    }

    [Fact]
    public void AddBlocksDataAccess_KeepsTheHostsOwnAccessor()
    {
        using var provider = Build(s => s.AddSingleton<IAccessPrincipalAccessor, HostAccessor>());

        Assert.IsType<HostAccessor>(provider.GetRequiredService<IAccessPrincipalAccessor>());
    }
}
