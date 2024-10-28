using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis
{
    public class BaseEntity
    {
        [BsonId]
        public string ItemId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastUpdatedDate { get; set; }
        public string? CreatedBy { get; set; }
        public string? LastUpdatedBy { get; set; }
        public List<string> OrganizationIds { get; set; } = new List<string>();
    }
}
