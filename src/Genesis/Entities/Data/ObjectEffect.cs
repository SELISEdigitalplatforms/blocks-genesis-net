// Moved from blocks-data server/Storage.DomainService/Shared/Enums/ObjectEffect.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Data;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectEffect
{
    Allow = 1,
    Deny = 2
}
