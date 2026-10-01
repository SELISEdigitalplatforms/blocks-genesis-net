namespace Blocks.Genesis;

/// <summary>
/// Key-value storage backed by the <c>KeyValueStores</c> collection.
/// </summary>
/// <remarks>
/// The collection carries a non-unique index on <c>Key</c>, so a key may map to one
/// document or to many. Two APIs sit on top of that, and a given key should be used
/// with one or the other, not both:
/// <list type="bullet">
/// <item><description>
/// <b>Single-value</b> (<see cref="SetAsync{T}"/>, <see cref="GetAsync{T}"/>,
/// <see cref="DeleteAsync"/>): the key identifies at most one document. <c>SetAsync</c>
/// upserts, so writing the same key twice overwrites rather than accumulating.
/// </description></item>
/// <item><description>
/// <b>Multi-value</b> (<see cref="AddAsync{T}"/>, <see cref="GetAllAsync{T}"/>,
/// <see cref="UpdateByIdAsync{T}"/>, <see cref="DeleteByIdAsync"/>): many documents
/// share a key. Reads are by key (optionally narrowed by tags); mutations are by
/// <c>ItemId</c>, which <see cref="AddAsync{T}"/> returns.
/// </description></item>
/// </list>
/// Mixing them on one key leaves <see cref="GetAsync{T}"/> returning an arbitrary
/// document and <see cref="SetAsync{T}"/> overwriting an arbitrary one.
/// </remarks>
public interface IKeyValueStore
{
    // ---------------------------------------------------------------- //
    // Single-value API - one document per key.                         //
    // ---------------------------------------------------------------- //

    /// <summary>Reads the value for <paramref name="key"/>, or <c>default</c> if absent.</summary>
    /// <remarks>If several documents share the key, which one is returned is undefined.</remarks>
    Task<T?> GetAsync<T>(string key, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Reads every value whose key starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<T>> GetByPrefixAsync<T>(string prefix, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Creates or overwrites the single document stored under <paramref name="key"/>.</summary>
    /// <remarks>Upsert. Use <see cref="AddAsync{T}"/> when a key should hold several values.</remarks>
    Task SetAsync<T>(string key, T value, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Deletes one document stored under <paramref name="key"/>.</summary>
    /// <remarks>If several documents share the key, which one is removed is undefined; use <see cref="DeleteAllAsync"/>.</remarks>
    Task<bool> DeleteAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Returns whether at least one document exists under <paramref name="key"/>.</summary>
    Task<bool> ExistsAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- //
    // Multi-value API - many documents may share a key.                 //
    // ---------------------------------------------------------------- //

    /// <summary>
    /// Inserts a new document under <paramref name="key"/> without touching any document
    /// already stored there, and returns its <c>ItemId</c>.
    /// </summary>
    /// <param name="tags">
    /// Optional service-specific labels, used to narrow reads in <see cref="GetAllAsync{T}"/>
    /// and <see cref="GetAllByPrefixAsync{T}"/>.
    /// </param>
    Task<string> AddAsync<T>(string key, T value, IEnumerable<string>? tags = null, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Reads every document under <paramref name="key"/>, optionally narrowed to those carrying all of <paramref name="tags"/>.</summary>
    Task<IReadOnlyList<KeyValueItem<T>>> GetAllAsync<T>(string key, IEnumerable<string>? tags = null, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Reads every document whose key starts with <paramref name="prefix"/>, optionally narrowed to those carrying all of <paramref name="tags"/>.</summary>
    Task<IReadOnlyList<KeyValueItem<T>>> GetAllByPrefixAsync<T>(string prefix, IEnumerable<string>? tags = null, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Reads a single document by its <c>ItemId</c>, or <c>null</c> if absent.</summary>
    Task<KeyValueItem<T>?> GetByIdAsync<T>(string itemId, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Replaces the value of the document with the given <c>ItemId</c>. Returns <c>false</c> if no such document exists.</summary>
    Task<bool> UpdateByIdAsync<T>(string itemId, T value, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Deletes the document with the given <c>ItemId</c>. Returns <c>false</c> if no such document exists.</summary>
    Task<bool> DeleteByIdAsync(string itemId, bool impersonated = true, CancellationToken cancellationToken = default);

    /// <summary>Deletes every document stored under <paramref name="key"/> and returns how many were removed.</summary>
    Task<long> DeleteAllAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default);
}
