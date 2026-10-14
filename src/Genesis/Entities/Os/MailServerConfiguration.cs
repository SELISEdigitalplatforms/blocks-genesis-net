// Moved from blocks-os server/Configuration.DomainService/Mail/Entities/MailServerConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public class MailServerConfiguration : BaseEntity
{
    public string Name { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
    public bool EnableSSL { get; set; }
    public string SenderName { get; set; }
    public string SenderAddress { get; set; }
    public string SenderUserName { get; set; }
    public string AccountPassword { get; set; }
    public bool UseDefaultCredentials { get; set; }
    public SmtpClient SmtpClient { get; set; } = SmtpClient.Default;
    public bool IsDefault { get; set; }
    public bool IsInbound { get; set; }
    public MailServiceProvider Provider { get; set; }
    public bool IsEnableSnsConfiguration { get; set; } = false;

    // The fields below are additive. Documents written before they existed deserialize with
    // AuthenticationType = Password and SecurityMode = Legacy — the zero values — and null
    // OAuth fields, which is exactly the pre-change behaviour. There is no migration.

    public MailAuthenticationType AuthenticationType { get; set; }

    public MailSecurityMode SecurityMode { get; set; }

    /// <summary>The Microsoft Entra tenant id, not the Blocks tenant id.</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>
    /// The Blocks Secrets id of the OAuth client secret. An opaque reference — the plaintext
    /// is never stored here, and this field is excluded from every API response so a browser
    /// cannot address the secret directly.
    /// </summary>
    public string? ClientSecretReference { get; set; }

    public string? MailboxAddress { get; set; }
}

/// <summary>
/// The SMTP/mail transport implementation used to deliver outgoing mail.
/// </summary>
public enum SmtpClient
{
    /// <summary>Use the platform default SMTP transport.</summary>
    Default = 0,

    /// <summary>Use Microsoft Graph as the mail transport (recommended for Microsoft 365 tenants).</summary>
    MsGraph = 1,

    /// <summary>Use the MailKit SMTP client (recommended for non-Microsoft providers).</summary>
    MsMailKit = 2,
}
