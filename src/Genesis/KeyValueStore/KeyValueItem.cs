namespace Blocks.Genesis;

/// <summary>
/// A single key-value document together with its identity and audit fields.
/// </summary>
/// <remarks>
/// The multi-value API returns this instead of a bare <typeparamref name="T"/> because
/// several documents can share the same <see cref="Key"/>. <see cref="ItemId"/> is the
/// document's <c>_id</c> and is the handle callers pass to
/// <see cref="IKeyValueStore.UpdateByIdAsync{T}"/> and
/// <see cref="IKeyValueStore.DeleteByIdAsync"/>.
/// </remarks>
public sealed record KeyValueItem<T>(
    string ItemId,
    string Key,
    T Value,
    IReadOnlyList<string> Tags,
    DateTime CreatedDate,
    DateTime LastUpdatedDate,
    string? CreatedBy,
    string? LastUpdatedBy,
    string OrganizationId);
