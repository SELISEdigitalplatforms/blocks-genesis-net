using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace Blocks.Genesis
{
    public class HealthServiceWorker : BackgroundService
    {
        private readonly ILogger<HealthServiceWorker> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly IMongoDatabase _database;
        private readonly ICacheClient _cacheClient;
        private readonly ITenants _tenants;
        private readonly MessageConfiguration _messageConfiguration;
        private readonly string _instanceId;
        private readonly string _serviceName;

        // Services to check
        private readonly ServiceBusAdministrationClient _serviceBusAdminClient;
        private IConnection _rabbitMqConnection;
        private readonly bool _useServiceBus;
        private readonly bool _useRabbitMq;

        public HealthServiceWorker(
            ILogger<HealthServiceWorker> logger,
            IBlocksSecret blocksSecret,
            ICacheClient cacheClient,
            MessageConfiguration messageConfiguration,
            ITenants tenants)
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _database = LmtConfiguration.GetMongoDatabase(blocksSecret.DatabaseConnectionString, LmtConfiguration.HealthDatabaseName);
            _cacheClient = cacheClient;
            _tenants = tenants;
            _messageConfiguration = messageConfiguration;

            _instanceId = Guid.NewGuid().ToString("n");
            _serviceName = messageConfiguration.ServiceName ?? "HealthService";

            // Determine which messaging services to check based on configuration
            _useServiceBus = !string.IsNullOrEmpty(messageConfiguration.Connection) &&
                             messageConfiguration.AzureServiceBusConfiguration != null;

            _useRabbitMq = messageConfiguration.RabbitMqConfiguration != null &&
                           !string.IsNullOrEmpty(messageConfiguration.Connection);

            // Initialize messaging clients according to configuration
            if (_useServiceBus)
            {
                _serviceBusAdminClient = new ServiceBusAdministrationClient(messageConfiguration.Connection);
            }

            if (_useRabbitMq)
            {
                InitializeRabbitMqConnection();
            }

            LmtConfiguration.CreateCollectionForHealth(_blocksSecret.DatabaseConnectionString);
        }

        private async Task InitializeRabbitMqConnection()
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(_messageConfiguration.Connection),
                    VirtualHost = "/",
                    ContinuationTimeout = TimeSpan.FromSeconds(62),
                    AutomaticRecoveryEnabled = true
                };

                _rabbitMqConnection = await factory.CreateConnectionAsync();
                _logger.LogInformation("RabbitMQ connection initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize RabbitMQ connection during startup");
                _rabbitMqConnection = null;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HealthServiceWorker started. Instance ID: {InstanceId}", _instanceId);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await PerformHealthChecksAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HealthServiceWorker ExecuteAsync");
            }
            finally
            {
                await CleanupResources();
                _logger.LogInformation("HealthServiceWorker stopped. Instance ID: {InstanceId}", _instanceId);
            }
        }

        private async Task CleanupResources()
        {
            try
            {
                if (_useRabbitMq && _rabbitMqConnection?.IsOpen == true)
                {
                    await _rabbitMqConnection.CloseAsync();
                    _rabbitMqConnection.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during resource cleanup");
            }
        }

        private async Task PerformHealthChecksAsync(CancellationToken stoppingToken)
        {
            try
            {
                var healthChecks = new List<Task<(string ServiceName, bool Status)>>
                {
                    CheckMongoDbHealth(),
                    Task.Run(() => CheckRedisHealth())
                };

                // Add messaging checks based on configuration
                if (_useServiceBus)
                {
                    healthChecks.Add(CheckServiceBusHealth());
                }

                if (_useRabbitMq)
                {
                    healthChecks.Add(Task.Run(() => CheckRabbitMqHealth()));
                }

                var healthResults = await Task.WhenAll(healthChecks);

                // Create health status document
                var healthData = new BsonDocument
                {
                    { "Instance", _instanceId },
                    { "ServiceName", _serviceName },
                    { "Timestamp", DateTime.UtcNow },
                    { "ServiceStatus", healthResults.All(r => r.Status) }
                };

                // Record individual service statuses
                foreach (var result in healthResults)
                {
                    healthData.Add(result.ServiceName, result.Status);
                }

                var collection = _database.GetCollection<BsonDocument>(LmtConfiguration.HealthDatabaseName);

                await collection.InsertOneAsync(healthData, cancellationToken: stoppingToken);

                _tenants.UpdateTenantCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during health checks for instance {InstanceId}", _instanceId);
            }
        }

        private async Task<(string ServiceName, bool Status)> CheckMongoDbHealth()
        {
            try
            {
                await _database.RunCommandAsync((Command<BsonDocument>)"{ping:1}");
                return ("DatabaseStatus", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MongoDB health check failed for instance {InstanceId}", _instanceId);
                return ("DatabaseStatus", false);
            }
        }

        private (string ServiceName, bool Status) CheckRedisHealth()
        {
            try
            {
                var pingResult = _cacheClient.CacheDatabase().Ping();
                return ("CacheStatus", pingResult.TotalMilliseconds >= 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis health check failed for instance {InstanceId}", _instanceId);
                return ("CacheStatus", false);
            }
        }

        private async Task<(string ServiceName, bool Status)> CheckServiceBusHealth()
        {
            try
            {
                // Just check if we can access the Service Bus namespace
                var namespaceProperties = await _serviceBusAdminClient.GetNamespacePropertiesAsync();
                return ("MessageStatus", namespaceProperties != null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure Service Bus health check failed for instance {InstanceId}", _instanceId);
                return ("MessageStatus", false);
            }
        }

        private (string ServiceName, bool Status) CheckRabbitMqHealth()
        {
            try
            {
                // If connection is not established or was lost, try to reconnect
                if (_rabbitMqConnection == null || !_rabbitMqConnection.IsOpen)
                {
                    InitializeRabbitMqConnection();
                }

                // Simply check if the connection is open
                var isHealthy = _rabbitMqConnection != null && _rabbitMqConnection.IsOpen;

                return ("MessagingStatus", isHealthy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RabbitMQ health check failed for instance {InstanceId}", _instanceId);
                return ("MessagingStatus", false);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await CleanupResources();
            await base.StopAsync(cancellationToken);
        }
    }
}