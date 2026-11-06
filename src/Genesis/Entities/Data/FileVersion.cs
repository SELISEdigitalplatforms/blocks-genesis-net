// Moved from blocks-data server/Storage.DomainService/Shared/Entities/FileVersion.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

[BsonIgnoreExtraElements]
public class FileVersion : BaseEntity
{
    public long No { get; private set; }
    public string? FileId { get; private set; }
    public long SizeInBytes { get; set; }
    public string? TenantId { get; private set; }

    /// <summary>
    /// Object key for this version. Null on legacy and migrated rows, where the
    /// caller falls back to the existing path computation.
    /// </summary>
    public string? StorageKey { get; set; }

    /// <summary>Who uploaded this version.</summary>
    public string? UploadedBy { get; set; }

    #region Phase1UploadSecurity

    /// <summary>
    /// Completion/verification lifecycle for this version. Null on legacy and migrated rows,
    /// which behave as <see cref="Blocks.Genesis.Entities.Data.FileVerificationStatus.Unverified"/> — see
    /// <see cref="GetEffectiveVerificationStatus"/>.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public FileVerificationStatus? FileVerificationStatus { get; set; }

    /// <summary>
    /// Snapshot, taken when the upload session was created, of whether completion is required
    /// for this version. Null on legacy rows behaves as <c>false</c> — see
    /// <see cref="GetEffectiveUploadCompletionRequired"/>.
    /// </summary>
    public bool? UploadCompletionRequired { get; set; }

    /// <summary>UTC instant at which the upload URL issued for this version expires.</summary>
    public DateTime? UploadUrlExpiresAtUtc { get; set; }

    /// <summary>Declared size, in bytes, supplied when the upload URL was requested.</summary>
    public long? ExpectedSizeInBytes { get; set; }

    /// <summary>Declared MIME content type supplied when the upload URL was requested.</summary>
    public string? ExpectedContentType { get; set; }

    /// <summary>Declared checksum supplied when the upload URL was requested.</summary>
    public string? ExpectedChecksum { get; set; }

    /// <summary>Algorithm that produced <see cref="ExpectedChecksum"/> (e.g. "MD5", "SHA256").</summary>
    public string? ChecksumAlgorithm { get; set; }

    /// <summary>Safe, non-sensitive reason recorded when completion sets <see cref="Enums.FileVerificationStatus.Rejected"/>.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// UTC instant until which a completion attempt holds an exclusive lease on this version.
    /// Lets <see cref="Services.IFileVersionRepository.TryClaimCompletionAsync"/> stop two concurrent
    /// completion calls from verifying the same version at once; a crash simply lets the lease
    /// expire and leaves a retryable <see cref="Enums.FileVerificationStatus.Quarantined"/> version.
    /// </summary>
    public DateTime? CompletionClaimedUntilUtc { get; set; }

    /// <summary>Resolves <see cref="FileVerificationStatus"/> to its effective, documented-default value for legacy rows.</summary>
    public FileVerificationStatus GetEffectiveVerificationStatus() =>
        FileVerificationStatus ?? Blocks.Genesis.Entities.Data.FileVerificationStatus.Unverified;

    /// <summary>Resolves <see cref="UploadCompletionRequired"/> to its effective, documented-default value for legacy rows.</summary>
    public bool GetEffectiveUploadCompletionRequired() => UploadCompletionRequired == true;

    #endregion

    private FileVersion() { }

    public static FileVersion CreateNew(string fileId, long no, FileVersionOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        return new FileVersion
        {
            FileId = fileId,
            No = options.LazyUpdate ? -no : no,
            ItemId = options.ItemId,
            TenantId = options.TenantId,
            CreatedDate = options.CreateDate,
            CreatedBy = options.CreatedBy,
            Tags = options.Tags,
            Language = options.Language,
            StorageKey = options.StorageKey,
            UploadedBy = options.UploadedBy,
            FileVerificationStatus = options.FileVerificationStatus,
            UploadCompletionRequired = options.UploadCompletionRequired,
            UploadUrlExpiresAtUtc = options.UploadUrlExpiresAtUtc,
            ExpectedSizeInBytes = options.ExpectedSizeInBytes,
            ExpectedContentType = options.ExpectedContentType,
            ExpectedChecksum = options.ExpectedChecksum,
            ChecksumAlgorithm = options.ChecksumAlgorithm
        };
    }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class FileVersionOptions
{
    public string ItemId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTime CreateDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public List<string>? Tags { get; set; }
    public string Language { get; set; } = "en";
    public bool LazyUpdate { get; set; } = false;
    public string? StorageKey { get; set; }
    public string? UploadedBy { get; set; }

    #region Phase1UploadSecurity

    public FileVerificationStatus? FileVerificationStatus { get; set; }
    public bool? UploadCompletionRequired { get; set; }
    public DateTime? UploadUrlExpiresAtUtc { get; set; }
    public long? ExpectedSizeInBytes { get; set; }
    public string? ExpectedContentType { get; set; }
    public string? ExpectedChecksum { get; set; }
    public string? ChecksumAlgorithm { get; set; }

    #endregion
}
