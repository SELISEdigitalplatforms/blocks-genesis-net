namespace Blocks.Genesis
{
    public sealed record TenantCacheUpdateMessage
    {
        public Tenant? Tenant { get; init; }
        public string? TenantId { get; init; }
        public string Action { get; init; } = Tenants.TenantCacheUpdateActionUpsert;
    }
}
