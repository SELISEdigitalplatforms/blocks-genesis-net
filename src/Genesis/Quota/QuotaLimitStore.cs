using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis;

/// <summary>
/// One meter's durable state, as blocks-os wrote it.
/// </summary>
/// <param name="Limit">Included ceiling for the period, or -1 for uncapped.</param>
/// <param name="Used">Allowance consumed so far this period.</param>
/// <param name="Purchased">Units bought outright. These carry past the boundary; the allowance does not.</param>
/// <param name="PeriodKey">Which 30-day period this row belongs to.</param>
public readonly record struct QuotaLimit(long Limit, long Used, long Purchased, string PeriodKey);

/// <summary>
/// Where Genesis reads a limit when the counter has none. blocks-os owns the limits and pushes
/// them; this is the cold path for a cache miss.
/// </summary>
public interface IQuotaLimitStore
{
    Task<QuotaLimit?> GetAsync(string tenantId, string meter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes counts back durably. One call per tenant covering every meter it touched, because N
    /// single-document updates per tenant per interval is a great deal of write amplification for
    /// no benefit.
    /// </summary>
    Task FlushUsageAsync(string tenantId, IReadOnlyDictionary<string, long> usageByMeter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the meter row from the ROOT database.
/// <para>
/// Subscription state never lives in a tenant database. Limits, usage and purchased units are
/// billing records, not application data: they belong to the platform, they outlive any single
/// environment, and an environment must not be able to read or write its own ceiling. Every query
/// here is filtered by <c>TenantId</c>, which is what makes the row environment-scoped without the
/// data sitting inside the environment.
/// </para>
/// </summary>
public sealed class MongoQuotaLimitStore : IQuotaLimitStore
{
    internal const string CollectionName = "ResourceLimits";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoQuotaLimitStore(IDbContextProvider dbContextProvider, IBlocksSecret blocksSecret)
    {
        ArgumentNullException.ThrowIfNull(dbContextProvider);
        ArgumentNullException.ThrowIfNull(blocksSecret);

        _collection = dbContextProvider
            .GetDatabase(blocksSecret.DatabaseConnectionString, blocksSecret.RootDatabaseName)
            .GetCollection<BsonDocument>(CollectionName);
    }

    private static FilterDefinition<BsonDocument> Row(string tenantId, string meter) =>
        Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("TenantId", tenantId),
            Builders<BsonDocument>.Filter.Eq("Resource", meter));

    public async Task<QuotaLimit?> GetAsync(string tenantId, string meter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(meter))
        {
            return null;
        }

        var row = await (await _collection.FindAsync(Row(tenantId, meter), cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new QuotaLimit(
            Limit: Read(row, "Limit", 0),
            Used: Read(row, "Usage", 0),
            Purchased: Read(row, "Purchased", 0),
            PeriodKey: row.TryGetValue("PeriodKey", out var period) ? period.AsString : string.Empty);
    }

    public async Task FlushUsageAsync(string tenantId, IReadOnlyDictionary<string, long> usageByMeter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || usageByMeter is null || usageByMeter.Count == 0)
        {
            return;
        }

        // $max, not $set: usage only rises within a period, so the write is idempotent and
        // order-independent. Any process may flush, and a late one cannot undo a later count.
        var writes = usageByMeter.Select(pair => new UpdateOneModel<BsonDocument>(
            Row(tenantId, pair.Key),
            Builders<BsonDocument>.Update.Max("Usage", pair.Value)))
            .ToList();

        await _collection.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static long Read(BsonDocument row, string field, long fallback) =>
        row.TryGetValue(field, out var value) && value.IsNumeric ? value.ToInt64() : fallback;
}
