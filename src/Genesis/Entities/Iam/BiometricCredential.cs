// Moved from blocks-iam server/Authentication.DomainService/Shared/Entities/BiometricCredential.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;

namespace Blocks.Genesis.Entities.Iam;

    [MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
    public class BiometricCredential : BaseEntity
    {
        public string? UserId { get; set; }
        public string? PhysicalAddress { get; set; }
        public bool IsActive { get; set; }
        public string? BiometricId { get; set; }
        public string? BiometricKey { get; set; }
        public BiometricType BiometricType { get; set; }
        public string? DeviceInformation { get; set; }
    }

    /// <summary>
/// Type of biometric factor enrolled on a device. Used by the biometric
/// authentication flow to decide which WebAuthn assertion type to request.
/// </summary>
public enum BiometricType
{
    /// <summary>Fingerprint reader (capacitive, optical, or ultrasonic).</summary>
    Fingerprint,

    /// <summary>Facial recognition (e.g. Face ID, Windows Hello face).</summary>
    Face,

    /// <summary>Iris scan (e.g. Samsung Iris).</summary>
    Iris,

    /// <summary>Retina scan (rare; high-security devices).</summary>
    Retina
}
