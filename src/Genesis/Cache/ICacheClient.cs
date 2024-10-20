using StackExchange.Redis;

namespace Blocks.Genesis
{
    public interface ICacheClient
    {
        /// <summary>
        /// Gets the Redis database instance.
        /// </summary>
        /// <returns>An instance of <see cref="IDatabase"/>.</returns>
        IDatabase CacheDatabase();

        /// <summary>
        /// Checks if a given key exists in the cache.
        /// </summary>
        /// <param name="key">The key to check for existence.</param>
        /// <returns>True if the key exists; otherwise, false.</returns>
        bool KeyExists(string key);

        /// <summary>
        /// Adds a string value to the cache.
        /// </summary>
        /// <param name="key">The key for the cache entry.</param>
        /// <param name="value">The string value to store.</param>
        /// <returns>True if the value was successfully added; otherwise, false.</returns>
        bool AddStringValue(string key, string value);

        /// <summary>
        /// Adds a string value to the cache with a specified lifespan.
        /// </summary>
        /// <param name="key">The key for the cache entry.</param>
        /// <param name="value">The string value to store.</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds.</param>
        /// <returns>True if the value was successfully added; otherwise, false.</returns>
        bool AddStringValue(string key, string value, long keyLifeSpan);

        /// <summary>
        /// Gets a string value from the cache by key.
        /// </summary>
        /// <param name="key">The key of the cache entry.</param>
        /// <returns>The string value if found; otherwise, null.</returns>
        string GetStringValue(string key);

        /// <summary>
        /// Removes a key from the cache.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        /// <returns>True if the key was removed; otherwise, false.</returns>
        bool RemoveKey(string key);

        /// <summary>
        /// Checks if a given key exists in the cache asynchronously.
        /// </summary>
        /// <param name="key">The key to check for existence.</param>
        /// <returns>A task that returns true if the key exists; otherwise, false.</returns>
        Task<bool> KeyExistsAsync(string key);

        /// <summary>
        /// Adds a string value to the cache asynchronously.
        /// </summary>
        /// <param name="key">The key for the cache entry.</param>
        /// <param name="value">The string value to store.</param>
        /// <returns>A task that returns true if the value was successfully added; otherwise, false.</returns>
        Task<bool> AddStringValueAsync(string key, string value);

        /// <summary>
        /// Adds a string value to the cache asynchronously with a specified lifespan.
        /// </summary>
        /// <param name="key">The key for the cache entry.</param>
        /// <param name="value">The string value to store.</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds.</param>
        /// <returns>A task that returns true if the value was successfully added; otherwise, false.</returns>
        Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan);

        /// <summary>
        /// Gets a string value from the cache asynchronously by key.
        /// </summary>
        /// <param name="key">The key of the cache entry.</param>
        /// <returns>A task that returns the string value if found; otherwise, null.</returns>
        Task<string> GetStringValueAsync(string key);

        /// <summary>
        /// Removes a key from the cache asynchronously.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        /// <returns>A task that returns true if the key was removed; otherwise, false.</returns>
        Task<bool> RemoveKeyAsync(string key);

        /// <summary>
        /// Adds a hash value to the cache.
        /// </summary>
        /// <param name="key">The key for the hash entry.</param>
        /// <param name="value">The collection of <see cref="HashEntry"/> to store.</param>
        /// <returns>True if the hash was successfully added; otherwise, false.</returns>
        bool AddHashValue(string key, IEnumerable<HashEntry> value);

        /// <summary>
        /// Adds a hash value to the cache with a specified lifespan.
        /// </summary>
        /// <param name="key">The key for the hash entry.</param>
        /// <param name="value">The collection of <see cref="HashEntry"/> to store.</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds.</param>
        /// <returns>True if the hash was successfully added; otherwise, false.</returns>
        bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan);

        /// <summary>
        /// Gets a hash value from the cache by key.
        /// </summary>
        /// <param name="key">The key of the hash entry.</param>
        /// <returns>An array of <see cref="HashEntry"/> if found; otherwise, an empty array.</returns>
        HashEntry[] GetHashValue(string key);

        /// <summary>
        /// Adds a hash value to the cache asynchronously.
        /// </summary>
        /// <param name="key">The key for the hash entry.</param>
        /// <param name="value">The collection of <see cref="HashEntry"/> to store.</param>
        /// <returns>A task that returns true if the hash was successfully added; otherwise, false.</returns>
        Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value);

        /// <summary>
        /// Adds a hash value to the cache asynchronously with a specified lifespan.
        /// </summary>
        /// <param name="key">The key for the hash entry.</param>
        /// <param name="value">The collection of <see cref="HashEntry"/> to store.</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds.</param>
        /// <returns>A task that returns true if the hash was successfully added; otherwise, false.</returns>
        Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan);

        /// <summary>
        /// Gets a hash value from the cache asynchronously by key.
        /// </summary>
        /// <param name="key">The key of the hash entry.</param>
        /// <returns>A task that returns an array of <see cref="HashEntry"/> if found; otherwise, an empty array.</returns>
        Task<HashEntry[]> GetHashValueAsync(string key);
    }
}
