using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

/// <summary>
/// Mobile push (Firebase Cloud Messaging) settings of a tenant, stored in the tenant DB key-value store under
/// <see cref="KeyValueKey"/>. The service-account JSON is never stored here: it lives in the secret store under
/// <see cref="SecretName"/>. Replaces the unused <see cref="FirebaseConfiguration"/> collection.
/// </summary>
[BsonIgnoreExtraElements]
public class FirebasePushConfiguration
{
    /// <summary>Key-value store key of this record.</summary>
    public const string KeyValueKey = "firebase-config";

    /// <summary>Secret name used when none is set.</summary>
    public const string DefaultSecretName = "firebase-service-account";

    public string ProjectId { get; set; } = string.Empty;

    [BsonIgnoreIfDefault]
    public bool Enabled { get; set; }

    public string SecretName { get; set; } = DefaultSecretName;
}
