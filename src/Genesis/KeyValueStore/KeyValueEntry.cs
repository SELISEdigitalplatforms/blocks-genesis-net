using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis;

[BsonIgnoreExtraElements]
public sealed class KeyValueEntry : BaseEntity
{
    public string Key { get; set; } = string.Empty;

    public BsonValue Value { get; set; } = BsonNull.Value;
}
