// Moved from blocks-os server/Configuration.DomainService/Shared/Enums/MailSecurityMode.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Os;

/// <summary>
/// The transport security a mail configuration negotiates.
/// </summary>
/// <remarks>
/// <see cref="Legacy"/> is zero deliberately, and means "no explicit mode was chosen — fall
/// back to the record's <c>EnableSSL</c> flag". Pre-existing documents therefore keep the
/// exact behaviour they had before this field existed. Where a mode other than
/// <see cref="Legacy"/> is set it is authoritative and <c>EnableSSL</c> is ignored.
/// </remarks>
public enum MailSecurityMode
{
    /// <summary>Defer to <c>EnableSSL</c>.</summary>
    Legacy = 0,

    /// <summary>Plaintext.</summary>
    None = 1,

    /// <summary>Connect in the clear, then upgrade with STARTTLS.</summary>
    StartTls = 2,

    /// <summary>TLS from the first byte (implicit TLS).</summary>
    SslOnConnect = 3,
}
