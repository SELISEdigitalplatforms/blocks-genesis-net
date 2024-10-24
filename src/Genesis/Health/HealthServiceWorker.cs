using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis
{
    public class HealthServiceWorker : BackgroundService
    {
        private readonly ILogger<HealthServiceWorker> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly IMongoDatabase _database;
        private readonly ICacheClient _cacheClient;
        private readonly ServiceBusAdministrationClient _adminClient;

        public HealthServiceWorker(ILogger<HealthServiceWorker> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient, MessageConfiguration messageConfiguration)
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _database = LmtConfiguration.GetMongoDatabase(blocksSecret.DatabaseConnectionString, LmtConfiguration.HealthDatabaseName);
            _cacheClient = cacheClient;
            _adminClient = new ServiceBusAdministrationClient(messageConfiguration.Connection);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LmtConfiguration.CreateCollectionForHealth(_blocksSecret.DatabaseConnectionString);
            await CheckServiceBusHealth();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool isMongoDbHealthy = await CheckMongoDbHealth();
                    bool isRedisHealthy = CheckRedisHealth();
                    bool isServiceBusHealthy = await CheckServiceBusHealth();

                    var log = new BsonDocument
                        {
                            { "ServiceName", _blocksSecret.ServiceName },
                            { "Timestamp", DateTime.UtcNow },
                            { "ServiceStatus", true },
                            { "DatabaseStatus", isMongoDbHealthy },
                            { "CacheStatus", isRedisHealthy },
                            { "EventStatus", isServiceBusHealthy }
                        };

                    var collection = _database.GetCollection<BsonDocument>(LmtConfiguration.HealthDatabaseName);
                    await collection.InsertOneAsync(log);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to log health status");
                }

                // Wait for 60 seconds before the next health check
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }

        private async Task<bool> CheckMongoDbHealth()
        {
            try
            {
                await _database.RunCommandAsync((Command<BsonDocument>)"{ping:1}"); // Pinging MongoDB
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MongoDB health check failed");
                return false;
            }
        }

        private bool CheckRedisHealth()
        {
            try
            {
                var ping = _cacheClient.CacheDatabase().Ping(); // Pinging Redis
                return ping.TotalMilliseconds >= 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis health check failed");
                return false;
            }
        }

        private async Task<bool> CheckServiceBusHealth()
        {
            try
            {
                string queueName = $"{_blocksSecret.ServiceName}__Health";

                bool exists = await _adminClient.QueueExistsAsync(queueName);

                if (!exists)
                {
                    await _adminClient.CreateQueueAsync(queueName);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure Service Bus health check failed");
                return false;
            }
        }
    }
}
