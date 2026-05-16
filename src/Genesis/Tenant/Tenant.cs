using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    [BsonIgnoreExtraElements]
    public class Tenant: BaseEntity
    {
        public string TenantId { get; set; } = Guid.NewGuid().ToString("n").ToUpper();
        public bool IsAcceptBlocksTerms { get; set; }
        public bool IsUseBlocksExclusively { get; set; }
        public string? Name { get; set; }
        public string DBName { get; set; } = Guid.NewGuid().ToString("n");
        public List<Applications> Applications { get; set; } = new List<Applications>();
        public bool IsDisabled { get; set; }
        public required string DbConnectionString { get; set; }
        public string TenantSalt { get; set; } = Guid.NewGuid().ToString("n");
        public required JwtTokenParameters JwtTokenParameters { get; set; }
        public ThirdPartyJwtTokenParameters ThirdPartyJwtTokenParameters { get; set; } = new();
        public bool IsRootTenant { get; set; }
        public string Environment { get; set; } = string.Empty;
        public string TenantGroupId { get; set; } = string.Empty;
    }

    [BsonIgnoreExtraElements]
    public class Applications
    {
        public string Domain { get; set; } = string.Empty;
        public string CookieDomain { get; set; } = string.Empty;
        public bool IsDomainVerified { get; set; }
    }

}