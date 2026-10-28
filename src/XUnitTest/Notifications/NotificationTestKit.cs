using Blocks.Genesis;
using Blocks.Genesis.Entities.Iam;
using Blocks.Genesis.Entities.Os;
using Blocks.Genesis.Notifications;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;

namespace XUnitTest.Notifications;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Throwaway tenant and root databases on the local test Mongo (127.0.0.1:27017, as the key-value store tests).
/// </summary>
internal sealed class NotificationMongo : IAsyncDisposable
{
    public const string TenantId = "PROJECT1";
    public const string RootTenantId = "ROOT1";
    public const string Org = "org-a";
    public const string OtherOrg = "org-b";
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";

    private readonly MongoClient _client = new(MongoConnectionString);
    private readonly string _tenantDbName = $"notif_t_{Guid.NewGuid():N}";
    private readonly string _rootDbName = $"notif_r_{Guid.NewGuid():N}";

    public NotificationMongo()
    {
        TenantDb = _client.GetDatabase(_tenantDbName);
        RootDb = _client.GetDatabase(_rootDbName);

        DbContextProvider.Setup(p => p.GetDatabase(TenantId)).Returns(TenantDb);
        DbContextProvider.Setup(p => p.GetDatabase(RootTenantId)).Returns(RootDb);
        DbContextProvider.Setup(p => p.GetDatabase(MongoConnectionString, _rootDbName, It.IsAny<bool>())).Returns(RootDb);
        Secret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
        Secret.SetupGet(s => s.RootDatabaseName).Returns(_rootDbName);
        Tenants.Setup(t => t.GetTenantByID(RootTenantId)).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = RootTenantId,
            IsRootTenant = true,
            DbConnectionString = MongoConnectionString,
            JwtTokenParameters = new JwtTokenParameters { PrivateCertificatePassword = string.Empty, IssueDate = DateTime.UtcNow },
        });
    }

    public IMongoDatabase TenantDb { get; }
    public IMongoDatabase RootDb { get; }
    public Mock<IDbContextProvider> DbContextProvider { get; } = new();
    public Mock<IBlocksSecret> Secret { get; } = new();
    public Mock<ITenants> Tenants { get; } = new();
    public ManualTime Time { get; } = new();

    public MongoNotificationDirectory Directory() => new(DbContextProvider.Object, Secret.Object, Tenants.Object, Time);

    public Task AddUserAsync(string id, string[] organizations, Dictionary<string, List<string>>? roles = null,
        Dictionary<string, List<string>>? permissions = null, bool active = true, IMongoDatabase? database = null) =>
        (database ?? TenantDb).GetCollection<User>("Users").InsertOneAsync(new User
        {
            ItemId = id,
            Active = active,
            OrganizationIds = [.. organizations],
            Roles = roles ?? [],
            Permissions = permissions ?? [],
        });

    public Task AddPermissionAsync(string resource, string organizationId, params string[] roles) =>
        TenantDb.GetCollection<Permission>("Permissions").InsertOneAsync(new Permission
        {
            ItemId = Guid.NewGuid().ToString(),
            Resource = resource,
            OrganizationId = organizationId,
            Roles = [.. roles],
        });

    public Task AddSubscriptionAsync(string userId, string context, DateTime createdTime) =>
        TenantDb.GetCollection<Blocks.Genesis.Entities.Logic.NotificationSubscription>("NotificationSubscriptions").InsertOneAsync(new()
        {
            Id = Guid.NewGuid().ToString(),
            ConnectionId = Guid.NewGuid().ToString(),
            UserId = userId,
            CreatedTime = createdTime,
            Context = context,
        });

    public Task AddProjectPeopleAsync(string rootUserId, string tenantId) =>
        RootDb.GetCollection<BsonDocument>("ProjectPeoples").InsertOneAsync(new BsonDocument
        {
            { "_id", Guid.NewGuid().ToString() },
            { "UserId", rootUserId },
            { "TenantId", tenantId },
        });

    public async ValueTask DisposeAsync()
    {
        await _client.DropDatabaseAsync(_tenantDbName);
        await _client.DropDatabaseAsync(_rootDbName);
    }
}

internal static class Sample
{
    public static QueuedNotification Item(string? organizationId = NotificationMongo.Org, params string[] named) => new()
    {
        Id = Guid.NewGuid(),
        Name = "orders-paid",
        TenantId = NotificationMongo.TenantId,
        OrganizationId = organizationId,
        NamedUserIds = named,
        PayloadJson = "{\"order\":\"42\"}",
        QueuedAt = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc),
    };

    public static NotificationConfiguration Config(NotificationAudience? audience = null, NotificationScope scope = NotificationScope.App) => new()
    {
        ItemId = "c1",
        Name = "orders-paid",
        NotifyMethod = "ordersPaid",
        EnablePersistence = true,
        Scope = scope,
        Audience = audience,
    };
}
