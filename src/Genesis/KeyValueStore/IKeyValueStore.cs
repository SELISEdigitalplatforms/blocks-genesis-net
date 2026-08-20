namespace Blocks.Genesis;

public interface IKeyValueStore
{
    Task<T?> GetAsync<T>(string key, bool impersonated = true, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> GetByPrefixAsync<T>(string prefix, bool impersonated = true, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, bool impersonated = true, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default);
}
