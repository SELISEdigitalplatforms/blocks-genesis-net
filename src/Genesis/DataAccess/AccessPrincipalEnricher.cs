using System.Collections.Concurrent;
using System.Globalization;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis.DataAccess;

/// <summary>
/// Adds to a principal the Auth values that live in the tenant database, not in the token:
/// role-bound permissions (<see cref="AccessPrincipal.RolePermissions"/>) and access attributes
/// (<see cref="AccessPrincipal.AccessAttributes"/>). Reads only what the given policies use.
/// </summary>
public interface IAccessPrincipalEnricher
{
    /// <summary>
    /// The principal with the database-backed values the <paramref name="policies"/> read. Returns the same
    /// principal when no policy reads them, for the root tenant, and for a call without a user context.
    /// A database failure is not swallowed: evaluating without the values could grant what a deny policy refuses.
    /// </summary>
    Task<AccessPrincipal> EnrichAsync(AccessPrincipal principal, IEnumerable<DataAccessPolicy> policies, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IAccessPrincipalEnricher"/>: <c>Permissions</c> (role-bound, cached per tenant, organization and
/// role set for <see cref="DataAccessOptions.RolePermissionCacheDuration"/>) and <c>Users</c> (access attributes,
/// read per request) of the principal's tenant database.
/// </summary>
public sealed class AccessPrincipalEnricher : IAccessPrincipalEnricher
{
    /// <summary>Key in <c>User.Attributes</c> that holds the access attributes: <c>access[orgId][name] = string[]</c>.</summary>
    public const string AccessAttributesKey = "access";

    private const string PermissionsCollection = "Permissions";
    private const string UsersCollection = "Users";
    private const int MaxCacheEntries = 10_000;

    private readonly IDbContextProvider _dbContextProvider;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CachedPermissions> _cache = new(StringComparer.Ordinal);

    private sealed record CachedPermissions(IReadOnlyList<string> Resources, DateTimeOffset ExpiresAt);

    public AccessPrincipalEnricher(IDbContextProvider dbContextProvider, DataAccessOptions? options = null, TimeProvider? timeProvider = null)
    {
        _dbContextProvider = dbContextProvider;
        _cacheDuration = options?.RolePermissionCacheDuration ?? DataAccessOptions.DefaultRolePermissionCacheDuration;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AccessPrincipal> EnrichAsync(AccessPrincipal principal, IEnumerable<DataAccessPolicy> policies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.IsRootTenant || !principal.HasContext || string.IsNullOrWhiteSpace(principal.TenantId))
            return principal;

        var usage = PolicyAuthUsage.Of(policies);
        if (usage.IsEmpty)
            return principal;

        var database = _dbContextProvider.GetDatabase(principal.TenantId);
        var enriched = principal;
        if (usage.UsesPermissions)
            enriched = enriched with { RolePermissions = await RolePermissionsAsync(database, principal, cancellationToken).ConfigureAwait(false) };
        if (usage.AttributeNames.Count > 0)
            enriched = enriched with { AccessAttributes = await AccessAttributesAsync(database, principal, cancellationToken).ConfigureAwait(false) };
        return enriched;
    }

    private async Task<IReadOnlyList<string>> RolePermissionsAsync(IMongoDatabase database, AccessPrincipal principal, CancellationToken cancellationToken)
    {
        var roles = principal.Roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (roles.Length == 0)
            return [];

        var organizationId = principal.EffectiveOrganizationId;
        var key = string.Join('\u001f', [principal.TenantId!, organizationId, .. roles]);
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Resources;

        var filter = Builders<BsonDocument>.Filter.In("Roles", roles)
                     & OrganizationFilter(organizationId)
                     & Builders<BsonDocument>.Filter.Ne("IsArchived", true);
        var documents = await database.GetCollection<BsonDocument>(PermissionsCollection)
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("Resource"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> resources = documents
            .Select(d => d.TryGetValue("Resource", out var resource) && resource.IsString ? resource.AsString : null)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = new CachedPermissions(resources, now + _cacheDuration);
        return resources;
    }

    // Documents of the tenant-wide organization written before OrganizationId existed count as "default".
    private static FilterDefinition<BsonDocument> OrganizationFilter(string organizationId)
    {
        var builder = Builders<BsonDocument>.Filter;
        var sameOrganization = builder.Eq("OrganizationId", organizationId);
        return organizationId == AccessPrincipal.DefaultOrganizationId
            ? sameOrganization | builder.Eq("OrganizationId", BsonNull.Value)
            : sameOrganization;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> AccessAttributesAsync(IMongoDatabase database, AccessPrincipal principal, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(principal.UserId))
            return result;

        var path = "Attributes." + AccessAttributesKey;
        var user = await database.GetCollection<BsonDocument>(UsersCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", principal.UserId))
            .Project(Builders<BsonDocument>.Projection.Include(path))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var organization = Child(Child(Child(user, "Attributes"), AccessAttributesKey), principal.EffectiveOrganizationId);
        if (organization is null)
            return result;

        foreach (var element in organization)
        {
            if (element.Name != "_t" && !result.ContainsKey(element.Name))
                result[element.Name] = Strings(Unwrap(element.Value));
        }
        return result;
    }

    private static BsonDocument? Child(BsonDocument? document, string name) =>
        document is not null && document.TryGetValue(name, out var value) && Unwrap(value) is BsonDocument child ? child : null;

    // The attribute bag serializer may have stored a driver wrapper { _t, _v } around a value; the payload is _v.
    private static BsonValue Unwrap(BsonValue value) =>
        value is BsonDocument document && document.Contains("_t") && document.TryGetValue("_v", out var wrapped) ? wrapped : value;

    private static string[] Strings(BsonValue value) => value switch
    {
        BsonArray array => array.Select(Unwrap).Select(Text).Where(t => t is not null).Select(t => t!).ToArray(),
        _ => Text(value) is { } single ? [single] : []
    };

    private static string? Text(BsonValue value) => value.BsonType switch
    {
        BsonType.String => value.AsString,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 or BsonType.Boolean =>
            Convert.ToString(BsonTypeMapper.MapToDotNetValue(value), CultureInfo.InvariantCulture),
        _ => null
    };
}
