using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    [BsonIgnoreExtraElements]
    public record Tenant
    {
        [BsonId]
        public string ItemId { get; set; }  
        public string TenantId { get; set; }
        public string DBName { get; set; }
        public string ApplicationDomain { get; set; }
        public bool IsDisabled { get; set; }
        public string DbConnectionString { get; set; }
        public JwtTokenParameters JwtTokenParameters { get; set; }
    }
}