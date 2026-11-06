// Moved from blocks-logic server/Proxy.DomainService/Entities/ProxyVersionKind.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

/// <summary>
/// Category of a <see cref="ProxyVersionEntity"/>. Serialized to MongoDB and returned to the console
/// as its string name ("Create", "ConfigUpdate", "Toggle", "Revert", "Delete").
/// </summary>
public enum ProxyVersionKind
{
    /// <summary>The proxy was created (version 1).</summary>
    Create,

    /// <summary>Name / upstream / methods / headers / query were changed.</summary>
    ConfigUpdate,

    /// <summary>The proxy was enabled or disabled.</summary>
    Toggle,

    /// <summary>An earlier version's configuration was restored onto the live proxy.</summary>
    Revert,

    /// <summary>The proxy was deleted; this is the final version row retained for history.</summary>
    Delete,
}
