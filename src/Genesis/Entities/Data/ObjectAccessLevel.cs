// Moved from blocks-data server/Storage.DomainService/Shared/Enums/ObjectAccessLevel.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// The default access a File or FileDirectory grants when no explicit
/// <see cref="ObjectAccessPolicy"/> exists on it or its ancestors yet. Unset (null) preserves
/// pre-existing behaviour: every tenant user is allowed in, exactly as before this enum existed.
/// An explicit <see cref="ObjectAccessPolicy"/> grant always overrides this default.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectAccessLevel
{
    /// <summary>Only the creator has access until the item is explicitly shared.</summary>
    Creator = 1,

    /// <summary>Anyone in the creator's organization has access until narrowed by an explicit policy.</summary>
    Organization = 2
}
