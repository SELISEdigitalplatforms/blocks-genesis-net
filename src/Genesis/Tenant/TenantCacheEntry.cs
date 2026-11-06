namespace Blocks.Genesis;

/// <summary>
/// A tenant held in the in-memory cache and the time after which it must be read again.
/// </summary>
internal sealed record TenantCacheEntry(Tenant Tenant, DateTimeOffset ExpiresAtUtc);
