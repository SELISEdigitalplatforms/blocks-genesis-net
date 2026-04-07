using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public sealed class RedisClient : ICacheClient, IDisposable
    {
        private const string ErrorTag = "error";
        private const string ErrorMessageTag = "errorMessage";

        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly IDatabase _database;
        private readonly ActivitySource _activitySource;
        private readonly ISubscriber _subscriber;
        private readonly ConcurrentDictionary<string, Action<RedisChannel, RedisValue>> _subscriptions = new();

        private bool _disposed;

        public RedisClient(IConnectionMultiplexer connectionMultiplexer, ActivitySource activitySource)
        {
            _connectionMultiplexer = connectionMultiplexer ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
            _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
            _database = _connectionMultiplexer.GetDatabase();
            _subscriber = _connectionMultiplexer.GetSubscriber();
        }

        public IDatabase CacheDatabase() => _database;

        #region Synchronous Methods

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
            return _database.StringSet(key, value, TimeSpan.FromSeconds(keyLifeSpan));
        }

        public string? GetStringValue(string key)
        {
            return _database.StringGet(key);
        }

        public bool RemoveKey(string key)
        {
            return _database.KeyDelete(key);
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value)
        {
            var entries = value.ToArray();
            _database.HashSet(key, entries);
            return true;
        }

        public bool AddHashValue(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            var entries = value.ToArray();
            _database.HashSet(key, entries);
            return _database.KeyExpire(key, TimeSpan.FromSeconds(keyLifeSpan));
        }

        public HashEntry[] GetHashValue(string key)
        {
            return _database.HashGetAll(key);
        }

        #endregion

        #region Asynchronous Methods

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
            return await _database.StringSetAsync(key, value, TimeSpan.FromSeconds(keyLifeSpan));
        }

        public async Task<string?> GetStringValueAsync(string key)
        {
            return await _database.StringGetAsync(key);
        }

        public async Task<bool> RemoveKeyAsync(string key)
        {
            return await _database.KeyDeleteAsync(key);
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value)
        {
            var entries = value.ToArray();
            await _database.HashSetAsync(key, entries);
            return true;
        }

        public async Task<bool> AddHashValueAsync(string key, IEnumerable<HashEntry> value, long keyLifeSpan)
        {
            var entries = value.ToArray();
            await _database.HashSetAsync(key, entries);
            return await _database.KeyExpireAsync(key, TimeSpan.FromSeconds(keyLifeSpan));
        }

        public async Task<HashEntry[]> GetHashValueAsync(string key)
        {
            return await _database.HashGetAllAsync(key);
        }

        #endregion

        #region Pub/Sub Methods

        public async Task<long> PublishAsync(string channel, string message)
        {
            if (string.IsNullOrEmpty(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            try
            {
                return await _subscriber.PublishAsync(channel, message);
            }
            catch (Exception ex)
            {
                using var activity = _activitySource.StartActivity("Redis::Publish", ActivityKind.Producer, Activity.Current?.Context ?? default);
                activity?.SetTag("Channel", channel);
                activity?.SetTag(ErrorTag, true);
                activity?.SetTag(ErrorMessageTag, ex.Message);
                throw;
            }
        }

        public async Task SubscribeAsync(string channel, Action<RedisChannel, RedisValue> handler)
        {
            if (string.IsNullOrEmpty(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

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
                        messageActivity?.SetTag(ErrorTag, true);
                        messageActivity?.SetTag(ErrorMessageTag, ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                using var activity = _activitySource.StartActivity("Redis::Subscribe", ActivityKind.Consumer, Activity.Current?.Context ?? default);
                activity?.SetTag("Channel", channel);
                activity?.SetTag(ErrorTag, true);
                activity?.SetTag(ErrorMessageTag, ex.Message);
                _subscriptions.TryRemove(channel, out _);
                throw;
            }
        }

        public async Task UnsubscribeAsync(string channel)
        {
            if (string.IsNullOrEmpty(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            try
            {
                await _subscriber.UnsubscribeAsync(channel);
                _subscriptions.TryRemove(channel, out _);
            }
            catch (Exception ex)
            {
                using var activity = _activitySource.StartActivity("Redis::Unsubscribe", ActivityKind.Consumer, Activity.Current?.Context ?? default);
                activity?.SetTag("Channel", channel);
                activity?.SetTag(ErrorTag, true);
                activity?.SetTag(ErrorMessageTag, ex.Message);
                throw;
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

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
