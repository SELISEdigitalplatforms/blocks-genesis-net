// Moved from blocks-logic server/Proxy.DomainService/Entities/ProxyResponseMode.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

/// <summary>How the gateway treats the upstream response body before relaying it.</summary>
public enum ProxyResponseMode
{
    /// <summary>Relay the body byte-for-byte (default; today's behaviour).</summary>
    All,

    /// <summary>Project the body to the configured <c>ResponseInclude</c> paths (see ProxyResponseProjector).</summary>
    Select,
}
