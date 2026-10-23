// Moved from blocks-logic server/Mail.DomainService/Entities/MailBoxEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class MailBoxEntity
{
    [BsonId]
    public string ItemId { get; set; }
    public string MessageId { get; set; }
    public string? MailServerConfigurationId { get; set; } = String.Empty;
    public string Subject { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public string Body { get; set; }


    [BsonRepresentation(BsonType.String)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MailStatus Status { get; set; }
    
    public string Error { get; set; }
    public DateTime Date { get; set; }
    public string RawMime { get; set; }
    public bool IsInbound { get; set; }
}
