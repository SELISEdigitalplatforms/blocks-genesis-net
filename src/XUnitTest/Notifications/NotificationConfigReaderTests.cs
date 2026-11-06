using Blocks.Genesis;
using Blocks.Genesis.Entities.Os;
using Blocks.Genesis.Notifications;
using Moq;

namespace XUnitTest.Notifications;

public sealed class NotificationConfigReaderTests
{
    private const string Key = "notification-config:orders-paid";
    private readonly Mock<IKeyValueStore> _store = new();
    private readonly ManualTime _time = new();

    private NotificationConfigReader Reader() => new(_store.Object, _time);

    private void Tenant(NotificationConfiguration? configuration) =>
        _store.Setup(s => s.GetAsync<NotificationConfiguration>(Key, true, It.IsAny<CancellationToken>())).ReturnsAsync(configuration);

    private void Root(NotificationConfiguration? configuration) =>
        _store.Setup(s => s.GetAsync<NotificationConfiguration>(Key, false, It.IsAny<CancellationToken>())).ReturnsAsync(configuration);

    [Fact]
    public async Task TenantConfig_Wins()
    {
        Tenant(Sample.Config());
        Root(new NotificationConfiguration { Name = "root", IsBuiltIn = true });

        var configuration = await Reader().GetAsync("T1", "orders-paid", CancellationToken.None);

        Assert.Equal("orders-paid", configuration!.Name);
    }

    [Fact]
    public async Task RootFallback_UsesBuiltInOnly()
    {
        Tenant(null);
        Root(new NotificationConfiguration { Name = "orders-paid", IsBuiltIn = false });

        Assert.Null(await Reader().GetAsync("T1", "orders-paid", CancellationToken.None));

        Root(new NotificationConfiguration { Name = "orders-paid", IsBuiltIn = true });
        Assert.NotNull(await Reader().GetAsync("T1", "orders-paid", CancellationToken.None));
    }

    [Fact]
    public async Task CachedFor60Seconds_PerTenant()
    {
        Tenant(Sample.Config());
        var reader = Reader();

        await reader.GetAsync("T1", "orders-paid", CancellationToken.None);
        _time.Now = _time.Now.AddSeconds(59);
        await reader.GetAsync("T1", "orders-paid", CancellationToken.None);
        _store.Verify(s => s.GetAsync<NotificationConfiguration>(Key, true, It.IsAny<CancellationToken>()), Times.Once);

        await reader.GetAsync("T2", "orders-paid", CancellationToken.None);
        _time.Now = _time.Now.AddSeconds(2);
        await reader.GetAsync("T1", "orders-paid", CancellationToken.None);
        _store.Verify(s => s.GetAsync<NotificationConfiguration>(Key, true, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
