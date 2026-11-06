// Moved from blocks-logic server/Proxy.DomainService/Entities/HttpMethodType.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

/// <summary>The HTTP methods a proxy may accept. Persisted to Mongo as its string name.</summary>
public enum HttpMethodType
{
    Get,
    Post,
    Put,
    Patch,
    Delete,
}
