using StackExchange.Redis;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public sealed class RedisClient : ICacheClient
    {
        private readonly IDatabase _database;
        private readonly ActivitySource _activitySource;

        public RedisClient(IBlocksSecret blocksSecret, ActivitySource activitySource)
        {
            IConnectionMultiplexer connectionMultiplexer = ConnectionMultiplexer.Connect(blocksSecret.CacheConnectionString);
            _database = connectionMultiplexer.GetDatabase();
            _activitySource = activitySource;
        }

        public IDatabase CacheDatabase()
        {
            return _database;
        }

        public bool KeyExists(string key)
        {
            var activity = SetActivity(key, "KeyExists");
            var result = _database.KeyExists(key);
            activity?.Stop();
            return result;
        }

        public bool AddStringValue(string key, string value)
        {
            var activity = SetActivity(key, "AddStringValue");
            var result = _database.StringSet(key, value);
            activity?.Stop();
            return result;
        }

        public bool AddStringValue(string key, string value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddStringValue");
            _database.StringSet(key, value);
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = _database.KeyExpire(key, expireOn);
            activity?.Stop();
            return result;
        }

        public string GetStringValue(string key)
        {
            var activity = SetActivity(key, "GetStringValue");
            var result = _database.StringGet(key);
            activity?.Stop();
            return result;
        }

        public bool RemoveKey(string key)
        {
            var activity = SetActivity(key, "RemoveKey");
            var result = _database.KeyDelete(key);
            activity?.Stop();
            return result;
        }

        public async Task<bool> KeyExistsAsync(string key)
        {
            var activity = SetActivity(key, "KeyExists");
            var result = await _database.KeyExistsAsync(key);
            activity?.Stop();
            return result;
        }

        public async Task<bool> AddStringValueAsync(string key, string value)
        {
            var activity = SetActivity(key, "AddStringValue");
            var result = await _database.StringSetAsync(key, value);
            activity?.Stop();
            return result;
        }

        public async Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddStringValue");
            await _database.StringSetAsync(key, value);
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = await _database.KeyExpireAsync(key, expireOn);
            activity?.Stop();
            return result;
        }

        public async Task<string> GetStringValueAsync(string key)
        {
            var activity = SetActivity(key, "GetStringValue");
            var result = await _database.StringGetAsync(key);
            activity?.Stop();
            return result;
        }

        public async Task<bool> RemoveKeyAsync(string key)
        {
            var activity = SetActivity(key, "RemoveKey");
            var result = await _database.KeyDeleteAsync(key);
            activity?.Stop();
            return result;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value)
        {
            var activity = SetActivity(key, "AddHashValue");
            _database.HashSet(key, value.ToArray());

            activity?.Stop();

            return true;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddHashValue");
            _database.HashSet(key, value.ToArray());
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = _database.KeyExpire(key, expireOn);
            activity?.Stop();
            return result;
        }

        public HashEntry[] GetHashValue(string key)
        {
            var activity = SetActivity(key, "GetHashValue");
            var result = _database.HashGetAll(key);
            activity?.Stop();
            return result;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value)
        {
            var activity = SetActivity(key, "AddHashValue");
            await _database.HashSetAsync(key, value.ToArray());
            activity?.Stop();
            return true;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddHashValue");
            await _database.HashSetAsync(key, value.ToArray());
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = await _database.KeyExpireAsync(key, expireOn);

            activity?.Stop();
            return result;
        }

        public async Task<HashEntry[]> GetHashValueAsync(string key)
        {
            var activity = SetActivity(key, "GetHashValue");

            var result = await _database.HashGetAllAsync(key);

            activity?.Stop();

            return result;
        }

        private Activity? SetActivity(string key, string operation)
        {
            var currentActivity = Activity.Current;
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity($"Redis::{operation}", ActivityKind.Producer, currentActivity?.Context ?? default);

            activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
            activity?.SetTag("Key", key);

            return activity;
        }

    }
}
