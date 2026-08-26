using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Delegation;

/// <summary>
/// Send-side rules: no authenticated user means no grant, and a worker-originated send carries the
/// version material forward from the grant it already holds.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class DelegationGrantFactoryTests : IDisposable
{
    private readonly bool _originalTestMode = BlocksContext.IsTestMode;

    public DelegationGrantFactoryTests()
    {
        BlocksContext.IsTestMode = true;
    }

    public void Dispose()
    {
        BlocksContext.SetContext(null);
        DelegatedTokenContext.Clear();
        BlocksContext.IsTestMode = _originalTestMode;
    }

    private static (DelegationGrantFactory Factory, Mock<IDelegationGrantStore> Store) CreateFactory()
    {
        var store = new Mock<IDelegationGrantStore>();
        var factory = new DelegationGrantFactory(store.Object, new Mock<ILogger<DelegationGrantFactory>>().Object);
        return (factory, store);
    }

    private static BlocksContext Context(bool authenticated, string userId = "user-1", string tenantId = "tenant-1")
        => BlocksContext.Create(
            tenantId: tenantId, roles: ["admin"], userId: userId, isAuthenticated: authenticated,
            requestUri: "https://unit.test", organizationId: "org-1", expireOn: DateTime.UtcNow.AddHours(1),
            email: null, permissions: null, userName: null, phoneNumber: null, displayName: null,
            oauthToken: null, originalTenantId: tenantId);

    [Fact]
    public async Task CreateForSendAsync_ShouldReturnNull_WhenThereIsNoContext()
    {
        var (factory, store) = CreateFactory();
        BlocksContext.SetContext(null);

        Assert.Null(await factory.CreateForSendAsync());
        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldReturnNull_WhenTheUserIsNotAuthenticated()
    {
        var (factory, store) = CreateFactory();
        BlocksContext.SetContext(Context(authenticated: false));

        Assert.Null(await factory.CreateForSendAsync());
        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldReturnNull_WhenContextHasNoUserId()
    {
        var (factory, store) = CreateFactory();
        BlocksContext.SetContext(Context(authenticated: true, userId: string.Empty));

        Assert.Null(await factory.CreateForSendAsync());
        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldReturnNull_WhenNoVersionMaterialIsAvailable()
    {
        // Authenticated context, but no HttpContext claims and no held grant: nothing to put in the record.
        var (factory, store) = CreateFactory();
        BlocksContext.SetContext(Context(authenticated: true));
        DelegatedTokenContext.Clear();

        Assert.Null(await factory.CreateForSendAsync());
        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldChainFromTheHeldGrant_ForWorkerOriginatedSends()
    {
        var (factory, store) = CreateFactory();
        var heldGrant = DelegationTestDoubles.SampleGrantId('b');
        var newGrant = DelegationTestDoubles.SampleGrantId('c');

        store.Setup(s => s.GetAsync(heldGrant)).ReturnsAsync(new DelegationGrantRecord
        {
            TenantId = "tenant-1", UserId = "user-1", OrganizationId = "org-1", TokenVersion = "4", SecurityStamp = "stamp-4"
        });
        store
            .Setup(s => s.CreateAsync(It.IsAny<BlocksContext>(), "4", "stamp-4", It.IsAny<TimeSpan?>()))
            .ReturnsAsync(newGrant);

        BlocksContext.SetContext(Context(authenticated: true));
        DelegatedTokenContext.Set(heldGrant);

        Assert.Equal(newGrant, await factory.CreateForSendAsync());
        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), "4", "stamp-4", It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldForwardTheTtlOverride()
    {
        var (factory, store) = CreateFactory();
        var heldGrant = DelegationTestDoubles.SampleGrantId('d');

        store.Setup(s => s.GetAsync(heldGrant)).ReturnsAsync(new DelegationGrantRecord
        {
            TenantId = "tenant-1", UserId = "user-1", TokenVersion = "1", SecurityStamp = "s"
        });
        store
            .Setup(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), TimeSpan.FromHours(9)))
            .ReturnsAsync(DelegationTestDoubles.SampleGrantId('e'));

        BlocksContext.SetContext(Context(authenticated: true));
        DelegatedTokenContext.Set(heldGrant);

        await factory.CreateForSendAsync(TimeSpan.FromHours(9));

        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), TimeSpan.FromHours(9)), Times.Once);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldNotChain_WhenTheHeldGrantBelongsToAnotherTenant()
    {
        var (factory, store) = CreateFactory();
        var heldGrant = DelegationTestDoubles.SampleGrantId('f');

        store.Setup(s => s.GetAsync(heldGrant)).ReturnsAsync(new DelegationGrantRecord
        {
            TenantId = "other-tenant", UserId = "user-1", TokenVersion = "1", SecurityStamp = "s"
        });

        BlocksContext.SetContext(Context(authenticated: true, tenantId: "tenant-1"));
        DelegatedTokenContext.Set(heldGrant);

        Assert.Null(await factory.CreateForSendAsync());
        store.Verify(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task CreateForSendAsync_ShouldReturnNull_WhenTheStoreThrows()
    {
        // A send must not fail because delegation could not be set up.
        var (factory, store) = CreateFactory();
        var heldGrant = DelegationTestDoubles.SampleGrantId('a');

        store.Setup(s => s.GetAsync(heldGrant)).ReturnsAsync(new DelegationGrantRecord
        {
            TenantId = "tenant-1", UserId = "user-1", TokenVersion = "1", SecurityStamp = "s"
        });
        store
            .Setup(s => s.CreateAsync(It.IsAny<BlocksContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new TimeoutException("redis down"));

        BlocksContext.SetContext(Context(authenticated: true));
        DelegatedTokenContext.Set(heldGrant);

        Assert.Null(await factory.CreateForSendAsync());
    }

    [Fact]
    public void DelegatedTokenContext_ShouldRejectMalformedIds()
    {
        DelegatedTokenContext.Set("dg_short");
        Assert.False(DelegatedTokenContext.HasGrant);
        Assert.Null(DelegatedTokenContext.Current);

        var valid = DelegationTestDoubles.SampleGrantId();
        DelegatedTokenContext.Set(valid);
        Assert.True(DelegatedTokenContext.HasGrant);
        Assert.Equal(valid, DelegatedTokenContext.Current);

        DelegatedTokenContext.Clear();
        Assert.False(DelegatedTokenContext.HasGrant);
    }
}
