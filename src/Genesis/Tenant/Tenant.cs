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
        
    }
}