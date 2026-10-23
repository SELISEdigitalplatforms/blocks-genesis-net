// Moved from blocks-logic server/Proxy.DomainService/Entities/ProxyFieldChange.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

/// <summary>
/// One field-level edit recorded on a <see cref="ProxyVersionEntity"/>. A single Save that touches three
/// fields produces one version row with three of these. Drives the console's per-field
/// <c>- old</c> / <c>+ new</c> history diff and the inverse-patch <em>Revert</em>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ProxyFieldChange
{
    /// <summary>
    /// Stable field address: <c>"name"</c> | <c>"upstream"</c> | <c>"enabled"</c> | <c>"methods"</c>
    /// | <c>"header:&lt;key&gt;"</c> | <c>"query:&lt;key&gt;"</c> (later: <c>"method:&lt;M&gt;:header:&lt;key&gt;"</c> ...).
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Display label, e.g. <c>"methods"</c>, <c>"header Authorization"</c>.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>RAW previous value; <c>null</c> ⇒ the field / row did not exist before.</summary>
    public string? Before { get; set; }

    /// <summary>RAW resulting value; <c>null</c> ⇒ the field / row was removed.</summary>
    public string? After { get; set; }
}
