using StackExchange.Redis;

namespace Blocks.Genesis
{
    public interface ICacheClient
    {
        public IDatabase CacheDatabase();
        public bool KeyExists(string key);
        public bool AddStringValue(string key, string value);
        public bool AddStringValue(string key, string value, long keyLifeSpan);
        public string GetStringValue(string key);
        public bool RemoveKey(string key);
        public Task<bool> KeyExistsAsync(string key);
        public Task<bool> AddStringValueAsync(string key, string value);
        public Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan);
        public Task<string> GetStringValueAsync(string key);
        public Task<bool> RemoveKeyAsync(string key);
        public bool AddHashValue(string key, IEnumerable<HashEntry> value);
        public bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan);
        public HashEntry[] GetHashValue(string key);
        public Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value);
        public Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan);
        public Task<HashEntry[]> GetHashValueAsync(string key);

    }
}
