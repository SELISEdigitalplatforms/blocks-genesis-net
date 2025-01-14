using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    [BsonIgnoreExtraElements]
    public class Tenant: BaseEntity
    {
        public string TenantId { get; set; } = Guid.NewGuid().ToString("n");
        public bool IsAcceptBlocksTerms { get; set; }
        public bool IsUseBlocksExclusively { get; set; }
        public bool IsProduction { get; set; }
        public string? Name { get; set; }
        public string DBName { get; set; } = Guid.NewGuid().ToString("n");
        public required string ApplicationDomain { get; set; }
        public string CookieDomain { get; set; } = string.Empty;
        public bool IsDisabled { get; set; }
        public required string DbConnectionString { get; set; }
        public string PasswordStrengthCheckerRegex { get; set; } = "^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)(?=.*[@$!%*?&])[A-Za-z\\d@$!%*?&]{8,}$";
        public string PasswordSalt { get; set; } = Guid.NewGuid().ToString("n");
        public required JwtTokenParameters JwtTokenParameters { get; set; }
        public List<string> AllowedGrantType { get; set; } = new List<string>();
        public int AccessTokenValidForNumberMinutes { get; init; } = 7;
        public int RefreshTokenValidForNumberMinutes { get; init; } = 30;
        public int RememberMeRefreshTokenValidForNumberMinutes { get; init; } = 30 * 60 * 24;
        public int GetNumberOfWrongAttemptsToLockTheAccount { get; set; } = 5;
        public int AccountLockDurationInMinutes { get; set; } = 5;
        public bool IsRootTenant { get; set; } 
        public bool IsCookieEnable { get; set; }

    }
}