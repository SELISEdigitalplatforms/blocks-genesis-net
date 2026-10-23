// Moved from blocks-iam server/Iam.DomainService/Shared/Enums/ResourceType.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Iam;

/// <summary>
/// Classification of the asset a permission or role guards. The value
/// drives policy evaluation: an Endpoint permission is checked by the API
/// gateway and a FrontendAction by the SPA.
/// The former DataProtection value (3) was removed on 2026-10-15; the number is
/// not reused. Old stored rows holding 3 still deserialize (int enum) but are
/// no longer a defined value.
/// </summary>
public enum ResourceType
{
    /// <summary>No resource type assigned (default for unconfigured rows).</summary>
    None,

    /// <summary>HTTP endpoint exposed by an API. Protected by middleware policy evaluation.</summary>
    Endpoint,

    /// <summary>UI action or menu item rendered by the frontend. Protected by the SPA's permission gate.</summary>
    FrontendAction
}
