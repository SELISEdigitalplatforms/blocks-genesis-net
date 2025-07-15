using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    [BsonIgnoreExtraElements]
    public class Tenant: BaseEntity
    {
        public string TenantId { get; set; } = Guid.NewGuid().ToString("n").ToUpper();
        public bool IsAcceptBlocksTerms { get; set; }
        public bool IsUseBlocksExclusively { get; set; }
        public bool IsProduction { get; set; }
        public string? Name { get; set; }
        public string DBName { get; set; } = Guid.NewGuid().ToString("n");
        public required string ApplicationDomain { get; set; }
        public List<string> AllowedDomains { get; set; } = new List<string>();
        public string CookieDomain { get; set; } = string.Empty;
        public bool IsDisabled { get; set; }
        public required string DbConnectionString { get; set; }
        public string TenantSalt { get; set; } = Guid.NewGuid().ToString("n");
        public required JwtTokenParameters JwtTokenParameters { get; set; }
        public bool IsRootTenant { get; set; }
        public bool IsCookieEnable { get; set; }
        public bool IsDomainVerified { get; set; }
        public List<Asset> Assets { get; set; }
        public string Environment { get; set; }
        public string TenantGroupId { get; set; }
    }
}