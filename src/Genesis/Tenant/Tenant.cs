using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    [BsonIgnoreExtraElements]
    public class Tenant: BaseEntity
    {
        public string TenantId { get; set; }
        public bool IsAcceptBlocksTerms { get; set; }
        public bool IsUseBlocksExclusively { get; set; }
        public bool IsProduction { get; set; }
        public string Name { get; set; }
        public string DBName { get; set; }
        public string ApplicationDomain { get; set; }   
        public bool IsDisabled { get; set; }
        public string DbConnectionString { get; set; }
        public string PasswordStrengthCheckerRegex { get; set; }
        public string PasswordSalt { get; set; }
        public JwtTokenParameters JwtTokenParameters { get; set; }
        public List<string> AllowedGrantType { get; set; } = new List<string>();
        public int AccessTokenValidForNumberMinutes { get; init; } = 7;
        public int RefreshTokenValidForNumberMinutes { get; init; } = 30;
        public int RememberMeRefreshTokenValidForNumberMinutes { get; init; } = 30 * 60 * 24;
        public int GetNumberOfWrongAttemptsToLockTheAccount { get; set; }
        public int AccountLockDurationInMinutes { get; set; }

    }
}