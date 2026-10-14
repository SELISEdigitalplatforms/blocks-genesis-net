// Moved from blocks-logic server/Proxy.DomainService/Entities/ProxyKeyValue.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

/// <summary>
/// A single header or query-string parameter that Blocks attaches when it forwards a call to the
/// upstream endpoint. Embedded in <see cref="ProxyDetailEntity"/> and <see cref="ProxyConfigSnapshot"/>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ProxyKeyValue
{
    /// <summary>Header / query-parameter name. 1..256 chars, no leading or trailing whitespace.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Stored verbatim, including any <c>{{$VAR.name}}</c> configuration-variable token; the token is
    /// resolved on the fly by the forwarder (from Blocks Secrets) and never persisted.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
