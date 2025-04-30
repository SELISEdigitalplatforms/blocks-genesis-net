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

            ISubscriber subscriber = connectionMultiplexer.GetSubscriber();
            _subscriber = subscriber;
        }

        public IDatabase CacheDatabase()
        {
            return _database;
        }

        #region Synchronous Methods

        public bool KeyExists(string key)
        {
            var activity = SetActivity(key, "KeyExists");
            var result = _database.KeyExists(key);

            return result;
        }

        public bool AddStringValue(string key, string value)
        {
            var activity = SetActivity(key, "AddStringValue");
            var result = _database.StringSet(key, value);

            return result;
        }

        public bool AddStringValue(string key, string value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddStringValue");
            _database.StringSet(key, value);
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = _database.KeyExpire(key, expireOn);

            return result;
        }

        public string GetStringValue(string key)
        {
            var activity = SetActivity(key, "GetStringValue");
            var result = _database.StringGet(key);

            return result;
        }

        public bool RemoveKey(string key)
        {
            var activity = SetActivity(key, "RemoveKey");
            var result = _database.KeyDelete(key);

            return result;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value)
        {
            var activity = SetActivity(key, "AddHashValue");
            _database.HashSet(key, value.ToArray());

            return true;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddHashValue");
            _database.HashSet(key, value.ToArray());
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = _database.KeyExpire(key, expireOn);

            return result;
        }

        public HashEntry[] GetHashValue(string key)
        {
            var activity = SetActivity(key, "GetHashValue");
            var result = _database.HashGetAll(key);

            return result;
        }

        #endregion

        #region Asynchronous Methods

        public async Task<bool> KeyExistsAsync(string key)
        {
            var activity = SetActivity(key, "KeyExists");
            var result = await _database.KeyExistsAsync(key);

            return result;
        }

        public async Task<bool> AddStringValueAsync(string key, string value)
        {
            var activity = SetActivity(key, "AddStringValue");
            var result = await _database.StringSetAsync(key, value);

            return result;
        }

        public async Task<bool> AddStringValueAsync(string key, string value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddStringValue");
            await _database.StringSetAsync(key, value);
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = await _database.KeyExpireAsync(key, expireOn);

            return result;
        }

        public async Task<string> GetStringValueAsync(string key)
        {
            var activity = SetActivity(key, "GetStringValue");
            var result = await _database.StringGetAsync(key);

            return result;
        }

        public async Task<bool> RemoveKeyAsync(string key)
        {
            var activity = SetActivity(key, "RemoveKey");
            var result = await _database.KeyDeleteAsync(key);

            return result;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value)
        {
            var activity = SetActivity(key, "AddHashValue");
            await _database.HashSetAsync(key, value.ToArray());

            return true;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            var activity = SetActivity(key, "AddHashValue");
            await _database.HashSetAsync(key, value.ToArray());
            var expireOn = DateTime.UtcNow.AddSeconds(keyLifeSpan);
            var result = await _database.KeyExpireAsync(key, expireOn);

            return result;
        }

        public async Task<HashEntry[]> GetHashValueAsync(string key)
        {
            var activity = SetActivity(key, "GetHashValue");
            var result = await _database.HashGetAllAsync(key);

            return result;
        }

        #endregion

        #region Pub/Sub Methods

        public async Task<long> PublishAsync(string channel, string message)
        {
            if (string.IsNullOrEmpty(channel))
                throw new ArgumentNullException(nameof(channel));

            var activity = SetActivity(channel, "Publish");
            try
            {
                var result = await _subscriber.PublishAsync(channel, message);
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

            var activity = SetActivity(channel, "Subscribe");
            try
            {
                // Store the handler to allow unsubscribing later
                _subscriptions.TryAdd(channel, handler);

                await _subscriber.SubscribeAsync(channel, (redisChannel, redisValue) =>
                {
                    using var messageActivity = _activitySource.StartActivity($"Redis::MessageReceived", ActivityKind.Consumer);
                    messageActivity?.SetTag("Channel", channel);

                    try
                    {
                        handler(redisChannel, redisValue);
                    }
                    catch (Exception ex)
                    {
                        messageActivity?.SetTag("error", true);
                        messageActivity?.SetTag("errorMessage", ex.Message);
                        // Log error or handle it based on your application's needs
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

            var activity = SetActivity(channel, "Unsubscribe");
            try
            {
                await _subscriber.UnsubscribeAsync(channel);
                _subscriptions.TryRemove(channel, out _);
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
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity($"Redis::{operation}", ActivityKind.Producer, currentActivity?.Context ?? default);

            activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
            activity?.SetTag("Key", key);

            return activity;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_disposed)
                return;

            // Unsubscribe from all channels
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