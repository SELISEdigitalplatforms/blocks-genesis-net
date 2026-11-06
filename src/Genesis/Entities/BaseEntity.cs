using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis;

[BsonIgnoreExtraElements]
public class BaseEntity
{
    [BsonId]
    public string ItemId { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }
    public string? CreatedBy { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Language { get; set; }
    public string? LastUpdatedBy { get; set; }
    public string OrganizationId { get; set; } = "default";
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Warned)]
    public List<string> Tags { get; set; } = [];

    // Per-record access lists. Null (the default) is neither stored nor written to JSON, so every existing
    // document and API response stays exactly as it is until a record is given access lists.

    /// <summary>Who may create this record.</summary>
    [BsonIgnoreIfNull]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [AllowRules(Validation = RuleAccess.Warned, Default = RuleAccess.Warned)]
    public OperationAccess? Create { get; set; }

    /// <summary>Who may read this record.</summary>
    [BsonIgnoreIfNull]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [AllowRules(Validation = RuleAccess.Warned, Default = RuleAccess.Warned)]
    public OperationAccess? Read { get; set; }

    /// <summary>Who may update this record.</summary>
    [BsonIgnoreIfNull]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [AllowRules(Validation = RuleAccess.Warned, Default = RuleAccess.Warned)]
    public OperationAccess? Update { get; set; }

    /// <summary>Who may delete this record.</summary>
    [BsonIgnoreIfNull]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [AllowRules(Validation = RuleAccess.Warned, Default = RuleAccess.Warned)]
    public OperationAccess? Delete { get; set; }
}
