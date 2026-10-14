// Moved from blocks-iam server/Captcha.Driver/Captcha/Secret.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

/// <summary>
/// MongoDB document representing a generic key/value secret. Captcha configuration
/// is stored in the <c>Secrets</c> collection under <see cref="CaptchaSecretKeys.SecretKey"/>.
/// </summary>
[BsonIgnoreExtraElements]
public class Secret : BaseEntity
{
    /// <summary>Secret identifier (e.g. <c>captcha</c>).</summary>
    [BsonElement("SecretKey")]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Free-form string key/value pairs.</summary>
    [BsonElement("KeyValuePairs")]
    public Dictionary<string, string> KeyValuePairs { get; set; } = new();

    /// <summary>Legacy alternative key/value map kept for backward compatibility.</summary>
    [BsonElement("KeyPairs")]
    public Dictionary<string, string> KeyPairs { get; set; } = new();
}
