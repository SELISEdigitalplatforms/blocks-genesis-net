using Blocks.Genesis;
using Blocks.Genesis.Notifications;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace XUnitTest.Notifications;

public sealed class NotificationRegistrationTests
{
    [Fact]
    public void WorkerHost_WithoutMapHub_GetsPublisherWorkerAndHubContext()
    {
        // A generic (worker) host, Development so the container is validated on build.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.CacheConnectionString).Returns("127.0.0.1:1,abortConnect=false,connectTimeout=100");
        var services = builder.Services;
        services.AddSingleton(secret.Object);
        services.AddSingleton(Mock.Of<ICacheClient>());
        services.AddSingleton(Mock.Of<ITenants>());
        services.AddSingleton(Mock.Of<IDbContextProvider>());
        services.AddSingleton(Mock.Of<IKeyValueStore>());
        services.AddSingleton(Mock.Of<IMessageClient>());

        services.AddBlocksNotifications();
        services.AddBlocksNotifications();
        using var host = builder.Build();
        var provider = host.Services;

        Assert.NotNull(provider.GetRequiredService<INotificationPublisher>());
        Assert.NotNull(provider.GetRequiredService<IHubContext<NotificationHub>>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<NotificationWorker>());
        Assert.Equal(3, provider.GetServices<INotificationChannelSender>().Count());
    }

    [Fact]
    public void HubTypeName_AndGroupNames_AreTheBackplaneContract()
    {
        // The Redis backplane channel names carry the hub's full type name: logic's hub and every sender must agree.
        Assert.Equal("Blocks.Genesis.Notifications.NotificationHub", typeof(NotificationHub).FullName);
        Assert.Equal("tenant:T1", NotificationGroups.ForTenant("T1"));
        Assert.Equal("tenant:T1:user:u1", NotificationGroups.ForTenantUser("T1", "u1"));
        Assert.Equal("tenant:T1:org:o1", NotificationGroups.ForTenantOrg("T1", "o1"));
    }
}
