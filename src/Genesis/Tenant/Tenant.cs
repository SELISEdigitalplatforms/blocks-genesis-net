using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    [BsonIgnoreExtraElements]
    public class Tenant
    {
        [BsonId]
        public string ItemId { get; set; }  
        public string TenantId { get; set; }
        public string ApplicationDomain { get; set; }
        public string DbConnectionString { get; set; }
        public JwtTokenParameters JwtTokenParameters { get; set; }
    }
}