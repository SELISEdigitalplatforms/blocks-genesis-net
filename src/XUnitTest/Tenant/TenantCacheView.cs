using Blocks.Genesis;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace XUnitTest.Tenant;

/// <summary>
/// Test view over the private tenant cache of <see cref="Tenants"/>. Entries set through the
/// indexer never expire, so tests that seed the cache directly are not affected by the lifetime.
/// </summary>
internal sealed class TenantCacheView : IEnumerable<KeyValuePair<string, Blocks.Genesis.Tenant>>
{
    private readonly ConcurrentDictionary<string, TenantCacheEntry> _entries;

    private TenantCacheView(ConcurrentDictionary<string, TenantCacheEntry> entries)
    {
        _entries = entries;
    }

    public static TenantCacheView Of(Tenants tenants)
    {
        var field = typeof(Tenants).GetField("_tenantCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return new TenantCacheView((ConcurrentDictionary<string, TenantCacheEntry>)field.GetValue(tenants)!);
    }

    public Blocks.Genesis.Tenant this[string tenantId]
    {
        get => _entries[tenantId].Tenant;
        set => _entries[tenantId] = new TenantCacheEntry(value, DateTimeOffset.MaxValue);
    }

    public int Count => _entries.Count;

    public bool ContainsKey(string tenantId) => _entries.ContainsKey(tenantId);

    public DateTimeOffset ExpiresAt(string tenantId) => _entries[tenantId].ExpiresAtUtc;

    public void Set(string tenantId, Blocks.Genesis.Tenant tenant, DateTimeOffset expiresAtUtc)
    {
        _entries[tenantId] = new TenantCacheEntry(tenant, expiresAtUtc);
    }

    public IEnumerator<KeyValuePair<string, Blocks.Genesis.Tenant>> GetEnumerator()
    {
        return _entries
            .Select(kvp => new KeyValuePair<string, Blocks.Genesis.Tenant>(kvp.Key, kvp.Value.Tenant))
            .GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
