// Moved from blocks-logic server/Scheduler.DomainService/Models/WebhookConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class WebhookConfiguration
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public Dictionary<string, string>? Headers { get; set; }
    public string? SigningSecret { get; set; }
}
