// Moved from blocks-data server/Storage.DomainService/Shared/Enums/ObjectPermission.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Data;

/// <summary>Ordered from least to most capable; numeric order is part of resolver semantics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectPermission
{
    View = 1,
    Download = 2,
    Edit = 3,
    Delete = 4,
    Manage = 5,
    Owner = 6
}
