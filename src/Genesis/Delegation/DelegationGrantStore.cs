using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text.Json;

namespace Blocks.Genesis;

/// <summary>
/// Redis-backed <see cref="IDelegationGrantStore"/>.
/// <para>
/// The record is written with its absolute TTL in a single call, so a grant can never exist
/// without an expiry. There is no sliding TTL, no heartbeat and no cleanup scheduler: the
/// happy path deletes the key after the message settles, and the TTL is the backstop.
/// </para>
/// </summary>
public sealed class DelegationGrantStore : IDelegationGrantStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly ICacheClient _cacheClient;
    private readonly ILogger<DelegationGrantStore> _logger;

    public DelegationGrantStore(ICacheClient cacheClient, ILogger<DelegationGrantStore> logger)
    {
        _cacheClient = cacheClient;
        _logger = logger;
    }

    public async Task<string> CreateAsync(BlocksContext ctx, string tokenVersion, string securityStamp, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(ctx.TenantId) || string.IsNullOrWhiteSpace(ctx.UserId))
        {
            throw new InvalidOperationException("A delegation grant requires both a tenant and an authenticated user.");
        }

        var record = new DelegationGrantRecord
        {
            TenantId = ctx.TenantId,
            UserId = ctx.UserId,
            OrganizationId = ctx.OrganizationId ?? string.Empty,
            TokenVersion = tokenVersion ?? string.Empty,
            SecurityStamp = securityStamp ?? string.Empty
        };

        var id = NewGrantId();
        var lifetime = NormalizeTtl(ttl);

        // Value and TTL are written in one call, so a grant can never exist without an expiry.
        // Every argument is named: the overload must be unambiguous, not chosen for us.
        await _cacheClient.CacheDatabase()
            .StringSetAsync(
                key: GrantKey(id),
                value: JsonSerializer.Serialize(record, SerializerOptions),
                expiry: lifetime,
                keepTtl: false,
                when: When.Always,
                flags: CommandFlags.None)
            .ConfigureAwait(false);

        // The id is a bearer credential: it is never logged, tagged on an Activity, or put in Baggage.
        DelegationGrantStoreLog.GrantCreated(_logger, record.TenantId, (long)lifetime.TotalSeconds);

        return id;
    }

    public async Task DeleteAsync(string id)
    {
        if (!IsWellFormed(id)) return;

        try
        {
            await _cacheClient.RemoveKeyAsync(GrantKey(id)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Removal is best effort. The absolute TTL still bounds the grant.
            DelegationGrantStoreLog.GrantDeleteFailed(_logger, ex);
        }
    }

    public async Task<DelegationGrantRecord?> GetAsync(string id)
    {
        if (!IsWellFormed(id)) return null;

        var json = await _cacheClient.GetStringValueAsync(GrantKey(id)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<DelegationGrantRecord>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            DelegationGrantStoreLog.GrantUnreadable(_logger, ex);
            return null;
        }
    }

    /// <summary>
    /// <c>dg_</c> + 64 lowercase hex chars from 32 cryptographically random bytes.
    /// Never <c>Guid.NewGuid()</c> — a grant id is a bearer credential, not an identifier.
    /// </summary>
    public static string NewGrantId()
    {
        var bytes = RandomNumberGenerator.GetBytes(DelegationConstants.GrantIdRandomBytes);
        return DelegationConstants.GrantIdPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsWellFormed(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!id.StartsWith(DelegationConstants.GrantIdPrefix, StringComparison.Ordinal)) return false;

        var hex = id.AsSpan(DelegationConstants.GrantIdPrefix.Length);
        if (hex.Length != DelegationConstants.GrantIdRandomBytes * 2) return false;

        foreach (var c in hex)
        {
            var isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowerHex) return false;
        }

        return true;
    }

    internal static string GrantKey(string id) => DelegationConstants.GrantKeyPrefix + id;

    private static TimeSpan NormalizeTtl(TimeSpan? ttl)
        => ttl is { } value && value > TimeSpan.Zero ? value : DelegationConstants.DefaultGrantTtl;
}

internal static partial class DelegationGrantStoreLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Debug, Message = "Delegation grant created for tenant {TenantId} with a {TtlSeconds}s lifetime.")]
    public static partial void GrantCreated(ILogger logger, string tenantId, long ttlSeconds);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Failed to delete a delegation grant. The absolute TTL will remove it.")]
    public static partial void GrantDeleteFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Stored delegation grant could not be deserialized.")]
    public static partial void GrantUnreadable(ILogger logger, Exception exception);
}
