using StackExchange.Redis;

namespace Blocks.Genesis
{
    /// <summary>
    /// Interface for cache client operations
    /// </summary>
    public interface ICacheClient
    {
        /// <summary>
        /// Gets the underlying Redis database instance
        /// </summary>
        /// <returns>The Redis database instance</returns>
        IDatabase CacheDatabase();

        #region Synchronous Methods

        /// <summary>
        /// Checks if a key exists in the cache
        /// </summary>
        /// <param name="key">The key to check</param>
        /// <returns>True if the key exists, false otherwise</returns>
        bool KeyExists(string key);

        /// <summary>
        /// Adds a string value to the cache
        /// </summary>
        /// <param name="key">The key for the value</param>
        /// <param name="value">The string value to add</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        bool AddStringValue(string key, string value);

        /// <summary>
        /// Adds a string value to the cache with an expiration
        /// </summary>
        /// <param name="key">The key for the value</param>
        /// <param name="value">The string value to add</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        bool AddStringValue(string key, string value, long keyLifeSpan);

        /// <summary>
        /// Gets a string value from the cache
        /// </summary>
        /// <param name="key">The key for the value</param>
        /// <returns>The string value if found, null otherwise</returns>
        string GetStringValue(string key);

        /// <summary>
        /// Removes a key from the cache
        /// </summary>
        /// <param name="key">The key to remove</param>
        /// <returns>True if the key was removed, false otherwise</returns>
        bool RemoveKey(string key);

        /// <summary>
        /// Adds a hash value to the cache
        /// </summary>
        /// <param name="key">The key for the hash</param>
        /// <param name="value">The hash entries to add</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        bool AddHashValue(string key, IEnumerable<HashEntry> value);

        /// <summary>
        /// Adds a hash value to the cache with an expiration
        /// </summary>
        /// <param name="key">The key for the hash</param>
        /// <param name="value">The hash entries to add</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan);

        /// <summary>
        /// Gets a hash value from the cache
        /// </summary>
        /// <param name="key">The key for the hash</param>
        /// <returns>The hash entries if found, empty array otherwise</returns>
        HashEntry[] GetHashValue(string key);

        #endregion

        #region Asynchronous Methods

        /// <summary>
        /// Checks if a key exists in the cache asynchronously
        /// </summary>
        /// <param name="key">The key to check</param>
        /// <returns>True if the key exists, false otherwise</returns>
        Task<bool> KeyExistsAsync(string key);

        /// <summary>
        /// Adds a string value to the cache asynchronously
        /// </summary>
        /// <param name="key">The key for the value</param>
        /// <param name="value">The string value to add</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        Task<bool> AddStringValueAsync(string key, string value);

        /// <summary>
        /// Adds a string value to the cache with an expiration asynchronously
        /// </summary>
        /// <param name="key">The key for the value</param>
        /// <param name="value">The string value to add</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan);

        /// <summary>
        /// Gets a string value from the cache asynchronously
        /// </summary>
        /// <param name="key">The key for the value</param>
        /// <returns>The string value if found, null otherwise</returns>
        Task<string> GetStringValueAsync(string key);

        /// <summary>
        /// Removes a key from the cache asynchronously
        /// </summary>
        /// <param name="key">The key to remove</param>
        /// <returns>True if the key was removed, false otherwise</returns>
        Task<bool> RemoveKeyAsync(string key);

        /// <summary>
        /// Adds a hash value to the cache asynchronously
        /// </summary>
        /// <param name="key">The key for the hash</param>
        /// <param name="value">The hash entries to add</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value);

        /// <summary>
        /// Adds a hash value to the cache with an expiration asynchronously
        /// </summary>
        /// <param name="key">The key for the hash</param>
        /// <param name="value">The hash entries to add</param>
        /// <param name="keyLifeSpan">The lifespan of the key in seconds</param>
        /// <returns>True if the operation was successful, false otherwise</returns>
        Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan);

        /// <summary>
        /// Gets a hash value from the cache asynchronously
        /// </summary>
        /// <param name="key">The key for the hash</param>
        /// <returns>The hash entries if found, empty array otherwise</returns>
        Task<HashEntry[]> GetHashValueAsync(string key);

        #endregion

        #region Pub/Sub Methods

        /// <summary>
        /// Publishes a message to a channel asynchronously
        /// </summary>
        /// <param name="channel">The channel to publish to</param>
        /// <param name="message">The message to publish</param>
        /// <returns>The number of clients that received the message</returns>
        Task<long> PublishAsync(string channel, string message);

        /// <summary>
        /// Subscribes to a channel asynchronously
        /// </summary>
        /// <param name="channel">The channel to subscribe to</param>
        /// <param name="handler">The handler to call when a message is received</param>
        /// <returns>A task that completes when the subscription is established</returns>
        Task SubscribeAsync(string channel, Action<RedisChannel, RedisValue> handler);

        /// <summary>
        /// Unsubscribes from a channel asynchronously
        /// </summary>
        /// <param name="channel">The channel to unsubscribe from</param>
        /// <returns>A task that completes when the unsubscription is completed</returns>
        Task UnsubscribeAsync(string channel);

        #endregion
    }
}