using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Iam;
using Blocks.Genesis.Entities.Logic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// Reads iam <c>Users</c> / <c>Permissions</c> and logic <c>NotificationSubscriptions</c> of the tenant DB, and os
/// <c>ProjectPeoples</c> of the root DB. Only the fields the rules need.
/// </summary>
internal sealed class MongoNotificationDirectory : INotificationDirectory
{
    internal const string UsersCollection = "Users";
    internal const string PermissionsCollection = "Permissions";
    internal const string SubscriptionsCollection = "NotificationSubscriptions";
    internal const string ProjectPeoplesCollection = "ProjectPeoples";
    internal static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromHours(3);
    private const int BatchSize = 1000;

    private readonly IDbContextProvider _dbContextProvider;
    private readonly IBlocksSecret _blocksSecret;
    private readonly ITenants _tenants;
    private readonly TimeProvider _timeProvider;

    public MongoNotificationDirectory(IDbContextProvider dbContextProvider, IBlocksSecret blocksSecret, ITenants tenants, TimeProvider timeProvider)
    {
        _dbContextProvider = dbContextProvider;
        _blocksSecret = blocksSecret;
        _tenants = tenants;
        _timeProvider = timeProvider;
    }

    public bool IsRootTenant(string tenantId) => _tenants.GetTenantByID(tenantId) is { IsRootTenant: true };

    public async Task<IReadOnlyList<DirectoryUser>> GetActiveUsersAsync(string tenantId, string? organizationId,
        IReadOnlyCollection<string>? userIds, CancellationToken cancellationToken)
    {
        var projection = Builders<User>.Projection
            .Include(u => u.ItemId)
            .Include(u => u.Roles)
            .Include(u => u.Permissions);

        var rows = await TenantDb(tenantId).GetCollection<User>(UsersCollection)
            .Find(ActiveUsersFilter(organizationId, userIds), new FindOptions { BatchSize = BatchSize })
            .Project<NotificationUserRow>(projection)
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ItemId))
            .Select(r => new DirectoryUser(r.ItemId, r.Roles ?? [], r.Permissions ?? []))
            .ToList();
    }

    public async Task<IReadOnlySet<string>> GetRolesGrantingAsync(string tenantId, string organizationId,
        IReadOnlyCollection<string> resources, CancellationToken cancellationToken)
    {
        if (resources.Count == 0)
        {
            return new HashSet<string>();
        }

        var builder = Builders<Permission>.Filter;
        // Ne(true), not Eq(false): rows written before IsArchived existed have no such field.
        var filter = builder.In(p => p.Resource, resources)
            & builder.Eq(p => p.OrganizationId, organizationId)
            & builder.Ne(p => p.IsArchived, true);
        var rows = await TenantDb(tenantId).GetCollection<Permission>(PermissionsCollection)
            .Find(filter)
            .Project<NotificationPermissionRow>(Builders<Permission>.Projection.Include(p => p.Roles))
            .ToListAsync(cancellationToken);

        return rows.SelectMany(r => r.Roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> GetSubscribedUserIdsAsync(string tenantId, IReadOnlyCollection<SubscriptionFilter> filters,
        CancellationToken cancellationToken)
    {
        var builder = Builders<NotificationSubscription>.Filter;
        var matches = filters.Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Context)).Select(f => FilterMatch(builder, f)).ToList();
        if (matches.Count == 0)
        {
            return new HashSet<string>();
        }

        var since = _timeProvider.GetUtcNow().UtcDateTime - SubscriptionLifetime;
        var filter = builder.Gte(s => s.CreatedTime, since) & builder.Or(matches);
        using var cursor = await TenantDb(tenantId).GetCollection<NotificationSubscription>(SubscriptionsCollection)
            .DistinctAsync(s => s.UserId, filter, cancellationToken: cancellationToken);
        var userIds = await cursor.ToListAsync(cancellationToken);

        return userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> GetConsoleMembersAsync(string tenantId, IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<string>();
        }

        var builder = Builders<BsonDocument>.Filter;
        var filter = builder.In("UserId", userIds) & builder.Eq("TenantId", tenantId);
        using var cursor = await RootDb().GetCollection<BsonDocument>(ProjectPeoplesCollection)
            .DistinctAsync<string>("UserId", filter, cancellationToken: cancellationToken);
        var members = await cursor.ToListAsync(cancellationToken);

        return members.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Same match the hub subscriptions always used: exact, or the subscription leaves Value, or ActionName and Value, empty.</summary>
    private static FilterDefinition<NotificationSubscription> FilterMatch(FilterDefinitionBuilder<NotificationSubscription> builder, SubscriptionFilter filter)
    {
        var context = builder.Eq(s => s.Context, filter.Context);
        var action = filter.ActionName ?? string.Empty;
        var value = filter.Value ?? string.Empty;
        return (context & builder.Eq(s => s.ActionName, action) & builder.Eq(s => s.Value, value))
            | (context & builder.Eq(s => s.ActionName, action) & builder.Eq(s => s.Value, string.Empty))
            | (context & builder.Eq(s => s.ActionName, string.Empty) & builder.Eq(s => s.Value, string.Empty));
    }

    /// <summary>Same active rule iam uses: Active, and Status Active or missing.</summary>
    private static FilterDefinition<User> ActiveUsersFilter(string? organizationId, IReadOnlyCollection<string>? userIds)
    {
        var builder = Builders<User>.Filter;
        var filter = builder.Eq(u => u.Active, true)
            & (builder.Eq(u => u.Status, UserLifecycleStatus.Active) | builder.Exists(u => u.Status, false));

        if (userIds is not null)
        {
            filter &= builder.In(u => u.ItemId, userIds);
        }

        if (organizationId is not null)
        {
            var member = builder.AnyEq(u => u.OrganizationIds, organizationId);
            filter &= organizationId == AccessPrincipal.DefaultOrganizationId
                ? member | builder.Size(u => u.OrganizationIds, 0) | builder.Eq(u => u.OrganizationIds, null)
                : member;
        }

        return filter;
    }

    private IMongoDatabase TenantDb(string tenantId) => _dbContextProvider.GetDatabase(tenantId);

    private IMongoDatabase RootDb() =>
        _dbContextProvider.GetDatabase(_blocksSecret.DatabaseConnectionString, _blocksSecret.RootDatabaseName);
}

/// <summary>The projected user fields; only Mongo sets them.</summary>
[BsonIgnoreExtraElements]
internal sealed class NotificationUserRow
{
    [BsonId]
    public string ItemId { get; set; } = string.Empty;
    public Dictionary<string, List<string>>? Roles { get; set; }
    public Dictionary<string, List<string>>? Permissions { get; set; }
}

/// <summary>The projected permission fields; only Mongo sets them.</summary>
[BsonIgnoreExtraElements]
internal sealed class NotificationPermissionRow
{
    [BsonId]
    public string ItemId { get; set; } = string.Empty;
    public List<string>? Roles { get; set; }
}
