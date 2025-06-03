using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public sealed class RedisClient : ICacheClient, IDisposable
    {
        private readonly IDatabase _database;
        private readonly ActivitySource _activitySource;
        private readonly ISubscriber _subscriber;
        private readonly ConcurrentDictionary<string, Action<RedisChannel, RedisValue>> _subscriptions = new();
        private bool _disposed = false;

        public RedisClient(IBlocksSecret blocksSecret, ActivitySource activitySource)
        {
            IConnectionMultiplexer connectionMultiplexer = ConnectionMultiplexer.Connect(blocksSecret.CacheConnectionString);
            _database = connectionMultiplexer.GetDatabase();
            _activitySource = activitySource;
            _subscriber = connectionMultiplexer.GetSubscriber();
        }

        public IDatabase CacheDatabase() => _database;

        #region Synchronous Methods

        public bool KeyExists(string key)
        {
            using var activity = SetActivity(key, "KeyExists");
            var result = _database.KeyExists(key);
            activity?.SetTag("Exists", result);
            return result;
        }

        public bool AddStringValue(string key, string value)
        {
            using var activity = SetActivity(key, "AddStringValue");
            activity?.SetTag("ValueLength", value?.Length ?? 0);
            var result = _database.StringSet(key, value);
            activity?.SetTag("Success", result);
            return result;
        }

        public bool AddStringValue(string key, string value, long keyLifeSpan)
        {
            using var activity = SetActivity(key, "AddStringValueWithTTL");
            activity?.SetTag("ValueLength", value?.Length ?? 0);
            activity?.SetTag("TTLSeconds", keyLifeSpan);
            _database.StringSet(key, value);
            var result = _database.KeyExpire(key, DateTime.UtcNow.AddSeconds(keyLifeSpan));
            activity?.SetTag("TTLSetSuccess", result);
            return result;
        }

        public string GetStringValue(string key)
        {
            using var activity = SetActivity(key, "GetStringValue");
            var result = _database.StringGet(key);
            activity?.SetTag("Hit", result.HasValue);
            return result;
        }

        public bool RemoveKey(string key)
        {
            using var activity = SetActivity(key, "RemoveKey");
            var result = _database.KeyDelete(key);
            activity?.SetTag("Removed", result);
            return result;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value)
        {
            using var activity = SetActivity(key, "AddHashValue");
            var entries = value.ToArray();
            activity?.SetTag("HashFieldCount", entries.Length);
            _database.HashSet(key, entries);
            return true;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            using var activity = SetActivity(key, "AddHashValueWithTTL");
            var entries = value.ToArray();
            activity?.SetTag("HashFieldCount", entries.Length);
            activity?.SetTag("TTLSeconds", keyLifeSpan);
            _database.HashSet(key, entries);
            var result = _database.KeyExpire(key, DateTime.UtcNow.AddSeconds(keyLifeSpan));
            activity?.SetTag("TTLSetSuccess", result);
            return result;
        }

        public HashEntry[] GetHashValue(string key)
        {
            using var activity = SetActivity(key, "GetHashValue");
            var result = _database.HashGetAll(key);
            activity?.SetTag("FieldCount", result.Length);
            return result;
        }

        #endregion

        #region Asynchronous Methods

        public async Task<bool> KeyExistsAsync(string key)
        {
            using var activity = SetActivity(key, "KeyExistsAsync");
            var result = await _database.KeyExistsAsync(key);
            activity?.SetTag("Exists", result);
            return result;
        }

        public async Task<bool> AddStringValueAsync(string key, string value)
        {
            using var activity = SetActivity(key, "AddStringValueAsync");
            activity?.SetTag("ValueLength", value?.Length ?? 0);
            var result = await _database.StringSetAsync(key, value);
            activity?.SetTag("Success", result);
            return result;
        }

        public async Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan)
        {
            using var activity = SetActivity(key, "AddStringValueWithTTLAsync");
            activity?.SetTag("ValueLength", value?.Length ?? 0);
            activity?.SetTag("TTLSeconds", keyLifeSpan);
            await _database.StringSetAsync(key, value);
            var result = await _database.KeyExpireAsync(key, DateTime.UtcNow.AddSeconds(keyLifeSpan));
            activity?.SetTag("TTLSetSuccess", result);
            return result;
        }

        public async Task<string> GetStringValueAsync(string key)
        {
            using var activity = SetActivity(key, "GetStringValueAsync");
            var result = await _database.StringGetAsync(key);
            activity?.SetTag("Hit", result.HasValue);
            return result;
        }

        public async Task<bool> RemoveKeyAsync(string key)
        {
            using var activity = SetActivity(key, "RemoveKeyAsync");
            var result = await _database.KeyDeleteAsync(key);
            activity?.SetTag("Removed", result);
            return result;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value)
        {
            using var activity = SetActivity(key, "AddHashValueAsync");
            var entries = value.ToArray();
            activity?.SetTag("HashFieldCount", entries.Length);
            await _database.HashSetAsync(key, entries);
            return true;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            using var activity = SetActivity(key, "AddHashValueWithTTLAsync");
            var entries = value.ToArray();
            activity?.SetTag("HashFieldCount", entries.Length);
            activity?.SetTag("TTLSeconds", keyLifeSpan);
            await _database.HashSetAsync(key, entries);
            var result = await _database.KeyExpireAsync(key, DateTime.UtcNow.AddSeconds(keyLifeSpan));
            activity?.SetTag("TTLSetSuccess", result);
            return result;
        }

        public async Task<HashEntry[]> GetHashValueAsync(string key)
        {
            using var activity = SetActivity(key, "GetHashValueAsync");
            var result = await _database.HashGetAllAsync(key);
            activity?.SetTag("FieldCount", result.Length);
            return result;
        }

        #endregion

        #region Pub/Sub Methods

        public async Task<long> PublishAsync(string channel, string message)
        {
            if (string.IsNullOrEmpty(channel))
                throw new ArgumentNullException(nameof(channel));

            using var activity = SetActivity(channel, "Publish");
            activity?.SetTag("MessageLength", message?.Length ?? 0);

            try
            {
                var result = await _subscriber.PublishAsync(channel, message);
                activity?.SetTag("SubscribersNotified", result);
                return result;
            }
            catch (Exception ex)
            {
                activity?.SetTag("error", true);
                activity?.SetTag("errorMessage", ex.Message);
                throw;
            }
        }

        public async Task SubscribeAsync(string channel, Action<RedisChannel, RedisValue> handler)
        {
            if (string.IsNullOrEmpty(channel))
                throw new ArgumentNullException(nameof(channel));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            using var activity = SetActivity(channel, "Subscribe");

            try
            {
                _subscriptions.TryAdd(channel, handler);
                await _subscriber.SubscribeAsync(channel, (redisChannel, redisValue) =>
                {
                    using var messageActivity = _activitySource.StartActivity($"Redis::MessageReceived", ActivityKind.Consumer);
                    messageActivity?.SetTag("Channel", channel);
                    messageActivity?.SetTag("MessageLength", redisValue.Length);

                    try
                    {
                        handler(redisChannel, redisValue);
                    }
                    catch (Exception ex)
                    {
                        messageActivity?.SetTag("error", true);
                        messageActivity?.SetTag("errorMessage", ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                activity?.SetTag("error", true);
                activity?.SetTag("errorMessage", ex.Message);
                _subscriptions.TryRemove(channel, out _);
                throw;
            }
        }

        public async Task UnsubscribeAsync(string channel)
        {
            if (string.IsNullOrEmpty(channel))
                throw new ArgumentNullException(nameof(channel));

            using var activity = SetActivity(channel, "Unsubscribe");

            try
            {
                await _subscriber.UnsubscribeAsync(channel);
                _subscriptions.TryRemove(channel, out _);
                activity?.SetTag("Unsubscribed", true);
            }
            catch (Exception ex)
            {
                activity?.SetTag("error", true);
                activity?.SetTag("errorMessage", ex.Message);
                throw;
            }
        }

        #endregion

        #region Helper Methods

        private Activity? SetActivity(string key, string operation)
        {
            var currentActivity = Activity.Current;
            var context = BlocksContext.GetContext();

            var activity = _activitySource.StartActivity($"Redis::{operation}", ActivityKind.Producer, currentActivity?.Context ?? default);

            activity?.SetTag("Key", key);

            return activity;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (var channel in _subscriptions.Keys)
            {
                _subscriber.Unsubscribe(channel);
            }
            _subscriptions.Clear();
            _disposed = true;
        }

        #endregion
    }
}
