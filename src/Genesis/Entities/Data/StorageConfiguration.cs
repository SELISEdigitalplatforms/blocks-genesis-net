// Moved from blocks-data server/Storage.DomainService/Shared/Entities/StorageConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class StorageConfiguration : BaseEntity
{
    public string Name { get; set; }
    public string ConnectionString { get; set; }
    public string SecretKey { get; set; }
    public string AccessKey { get; set; }
    public string StorageStrategy { get; set; }
    public string CloudStorageRegionEndPoint { get ; set ;  }

    #region LocalStorage

    public string Host { get; set; }
    public  string Port { get; set; }
    public  string UserName { get; set; }
    public  string Password { get; set; }
    public  string RemoteBasePath { get; set; }
    public string SftpSecretKey { get; set; }

    #endregion

    #region Phase1UploadSecurity

    /// <summary>Lifetime of a generated upload URL, in seconds. Null/missing resolves to <see cref="Constants.DefaultUploadUrlExpirySeconds"/>.</summary>
    public int? UploadUrlExpirySeconds { get; set; }

    /// <summary>Lifetime of a generated download URL, in seconds. Null/missing resolves to <see cref="Constants.DefaultDownloadUrlExpirySeconds"/>.</summary>
    public int? DownloadUrlExpirySeconds { get; set; }

    /// <summary>Maximum accepted upload size, in bytes. Null/missing resolves to <see cref="Constants.DefaultMaxFileSizeInBytes"/>.</summary>
    public long? MaxFileSizeInBytes { get; set; }

    /// <summary>
    /// Access modifiers for which upload completion (quarantine + synchronous verification) is required.
    /// Only <see cref="AccessModifier.Public"/> and <see cref="AccessModifier.Private"/> are honored; any
    /// other value is ignored. Null/missing resolves to an empty set (completion not required).
    /// Stored as strings (not the enum's default Int32 BSON representation) so Logic/OS configuration
    /// persistence can write plain access-modifier names into the same document without sharing this enum type.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public List<AccessModifier>? UploadCompletionRequiredFor { get; set; }





    #endregion

    public static string GetMaskedCloudStorageRegionEndPoint( string endPoint)
    {
        if (string.IsNullOrEmpty(endPoint))
            return string.Empty;

        ReadOnlySpan<char> span = endPoint.AsSpan();
        if (span.Length <= 2)
            return new string('*', span.Length);

        return $"{span[0]}{new string('*', span.Length - 2)}{span[^1]}";
    }
}
