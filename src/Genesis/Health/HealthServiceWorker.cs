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
        private readonly ITenants _tenants;

        public HealthServiceWorker(
            ILogger<HealthServiceWorker> logger,
            IBlocksSecret blocksSecret,
            ICacheClient cacheClient,
            MessageConfiguration messageConfiguration,
            ITenants tenants
        )
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _database = LmtConfiguration.GetMongoDatabase(blocksSecret.DatabaseConnectionString, LmtConfiguration.HealthDatabaseName);
            _cacheClient = cacheClient;
            _adminClient = new ServiceBusAdministrationClient(messageConfiguration.Connection);
            _tenants = tenants;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HealthServiceWorker started.");

            LmtConfiguration.CreateCollectionForHealth(_blocksSecret.DatabaseConnectionString);

            while (!stoppingToken.IsCancellationRequested)
            {
                await PerformHealthChecksAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }

            _logger.LogInformation("HealthServiceWorker stopped.");
        }

        private async Task PerformHealthChecksAsync(CancellationToken stoppingToken)
        {
            try
            {
                var healthResults = await Task.WhenAll(CheckMongoDbHealth(), Task.Run(CheckRedisHealth), CheckServiceBusHealth());

                // Log health status
                var log = new BsonDocument
                {
                    { "ServiceName", "HealthService" },
                    { "Timestamp", DateTime.UtcNow },
                    { "ServiceStatus", true },
                    { "DatabaseStatus", healthResults[0] },
                    { "CacheStatus", healthResults[1] },
                    { "EventStatus", healthResults[2] }
                };

                var collection = _database.GetCollection<BsonDocument>(LmtConfiguration.HealthDatabaseName);
                await collection.InsertOneAsync(log, cancellationToken: stoppingToken);

                // Refresh tenant cache
                _tenants.UpdateTenantCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during health checks.");
            }
        }

        private async Task<bool> CheckMongoDbHealth()
        {
            try
            {
                await _database.RunCommandAsync((Command<BsonDocument>)"{ping:1}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MongoDB health check failed.");
                return false;
            }
        }

        private bool CheckRedisHealth()
        {
            try
            {
                var pingResult = _cacheClient.CacheDatabase().Ping();
                return pingResult.TotalMilliseconds >= 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis health check failed.");
                return false;
            }
        }

        private async Task<bool> CheckServiceBusHealth()
        {
            try
            {
                const string queueName = "HealthQueue";

                if (!await _adminClient.QueueExistsAsync(queueName))
                {
                    await _adminClient.CreateQueueAsync(queueName);
                    _logger.LogInformation("Service Bus health queue created.");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure Service Bus health check failed.");
                return false;
            }
        }
    }
}
