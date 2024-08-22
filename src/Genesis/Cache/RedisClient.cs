using StackExchange.Redis;

namespace Blocks.Genesis
{
    public sealed class RedisClient : ICacheClient
    {
        private readonly IDatabase _database;

        public RedisClient(IBlocksSecret blocksSecret)
        {
            IConnectionMultiplexer connectionMultiplexer = ConnectionMultiplexer.Connect(blocksSecret.CacheConnectionString);
            _database = connectionMultiplexer.GetDatabase();
        }

        public IDatabase CacheDatabase()
        {
            return _database;
        }

        public bool KeyExists(string key)
        {
            return _database.KeyExists(key);
        }

        public bool AddStringValue(string key, string value)
        {
            return _database.StringSet(key, value);
        }

        public bool AddStringValue(string key, string value, long keyLifeSpan)
        {
            _database.StringSet(key, value);
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            return _database.KeyExpire(key, expireOn);
        }

        public string GetStringValue(string key)
        {
            return _database.StringGet(key);
        }

        public bool RemoveKey(string key)
        {
            return _database.KeyDelete(key);
        }

        public async Task<bool> KeyExistsAsync(string key)
        {
            return await _database.KeyExistsAsync(key);
        }

        public async Task<bool> AddStringValueAsync(string key, string value)
        {
            return await _database.StringSetAsync(key, value);
        }

        public async Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan)
        {
            await _database.StringSetAsync(key, value);
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            return await _database.KeyExpireAsync(key, expireOn);
        }

        public async Task<string> GetStringValueAsync(string key)
        {
            return await _database.StringGetAsync(key);
        }

        public async Task<bool> RemoveKeyAsync(string key)
        {
            return await _database.KeyDeleteAsync(key);
        }

    }
}
