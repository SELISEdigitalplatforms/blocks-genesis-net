// Moved from blocks-iam server/Iam.DomainService/SignupLinks/SignupLinkCredentialMode.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Iam;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignupLinkCredentialMode
{
    Passwordless = 0,
    PasswordRequired = 1
}
