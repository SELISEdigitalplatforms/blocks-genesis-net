using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis.Health
{
    public class GenesisHealthPingBackgroundService: BackgroundService
    {
        private readonly ILogger<GenesisHealthPingBackgroundService> _logger;
        private readonly IDbContextProvider _dbContextProvider;
        private readonly IBlocksSecret _blocksSecret;
        private readonly IDatabase _cacheDb;
        private readonly string _serviceName;
        private readonly string _connectionString;
        private readonly string _databaseName;
        private readonly string _configKey;
        private readonly string _blocksHealthCollectionName;
        private readonly int _cacheExpirationInMinutes;
        private readonly HttpClient _httpClient;


        private PeriodicTimer? _timer;
        private BlocksServicesHealthConfiguration? _currentConfig;

        public GenesisHealthPingBackgroundService(
            ILogger<GenesisHealthPingBackgroundService> logger,
            IDbContextProvider dbContextProvider,
            ICacheClient cacheClient,
            IBlocksSecret blocksSecret)
        {
            _logger = logger;
            _dbContextProvider = dbContextProvider;
            _blocksSecret = blocksSecret;
            _cacheDb = cacheClient.CacheDatabase();
            _serviceName = _blocksSecret.ServiceName;
            _connectionString = _blocksSecret.DatabaseConnectionString;
            _databaseName = _blocksSecret.RootDatabaseName;
            _configKey = $"GenesisHealthConfig:{_serviceName}";
            _cacheExpirationInMinutes = 15;
            _blocksHealthCollectionName = "BlocksServicesHealthConfigurations";
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Genesis.HealthPing/1.0");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) 
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            // Load initial configuration from database
            await LoadConfigurationFromCacheAsync();

            if (_currentConfig is null)
            {
                _logger.LogInformation("[{ServiceName}] Health check ping is not found.", _serviceName);
                return;
            }

            if (!_currentConfig.HealthCheckEnabled)
            {
                _logger.LogInformation("[{ServiceName}] Health check ping is disabled.", _serviceName);
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentConfig.Endpoint))
            {
                _logger.LogInformation("[{ServiceName}] Health check url is empty.", _serviceName);
                return;
            }

            ResetTimer();

            // Immediate first ping
            await PingAsync(stoppingToken);

            // Start both loops
            var pingTask = PingLoopAsync(stoppingToken);
            var refreshTask = ConfigRefreshLoopAsync(stoppingToken);

            await Task.WhenAll(pingTask, refreshTask);
        }


        private async Task ConfigRefreshLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(6), stoppingToken);

                    _logger.LogDebug("[{ServiceName}] Refreshing config from database", _serviceName);
                    await LoadConfigurationFromDatabaseAsync();
                    ResetTimer();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{ServiceName}] Failed to refresh config", _serviceName);
                }
            }
        }


        private async Task LoadConfigurationFromCacheAsync()
        {
            try
            {
                var cachedCert = _cacheDb.StringGet(_configKey);

                if (!cachedCert.HasValue)
                {
                    await LoadConfigurationFromDatabaseAsync();
                    
                    return;
                }
                _currentConfig = JsonSerializer.Deserialize<BlocksServicesHealthConfiguration>(cachedCert);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ServiceName}] Error loading health check configuration from database", _serviceName);
            }
        }

        private async Task LoadConfigurationFromDatabaseAsync()
        {
            try
            {
                var database = _dbContextProvider.GetDatabase(_connectionString,_databaseName);
                var genesisHealthConfigurationCollection = database.GetCollection<BlocksServicesHealthConfiguration>($"{_blocksHealthCollectionName}");

                var filter = Builders<BlocksServicesHealthConfiguration>.Filter.Eq(x =>x.ServiceName, _serviceName);
                BlocksServicesHealthConfiguration config = await genesisHealthConfigurationCollection.Find(filter).FirstOrDefaultAsync();
                if (config is not null)
                {
                    // Detect changes
                    var hasChanged = _currentConfig == null ||
                                   _currentConfig.HealthCheckEnabled != config.HealthCheckEnabled ||
                                   _currentConfig.Endpoint != config.Endpoint ||
                                   _currentConfig.PingIntervalSeconds != config.PingIntervalSeconds;

                    if (hasChanged)
                    {
                        _logger.LogInformation(
                            "[{ServiceName}] Config updated: Enabled={Enabled}, Interval={Interval}s",
                            _serviceName,
                            config.HealthCheckEnabled,
                            config.PingIntervalSeconds);
                    }
                    _currentConfig = config;
                    if (_currentConfig != null)
                    {
                        try
                        {
                            _cacheDb.StringSet(_configKey, JsonSerializer.Serialize(_currentConfig), TimeSpan.FromMinutes(_cacheExpirationInMinutes));
                        }
                        catch (Exception cacheEx)
                        {
                            _logger.LogWarning(cacheEx, "[{ServiceName}] Failed to write to cache, continuing with in-memory config", _serviceName);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("[{ServiceName}] No health check configuration found in database", _serviceName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,"[{ServiceName}] Error loading health check configuration from database", _serviceName);
            }
        }

        private async Task PingAsync(CancellationToken ct)
        {
            if (_currentConfig == null || string.IsNullOrWhiteSpace(_currentConfig.Endpoint))
            {
                _logger.LogWarning("[{ServiceName}] Cannot ping: config or endpoint is null", _serviceName);
                return;
            }
            try
            {
                using var response = await _httpClient.GetAsync(_currentConfig?.Endpoint, ct);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Ping success ({response.StatusCode}) for service {_serviceName} for url {MaskUrl(_currentConfig.Endpoint)}");
                    return;
                }

                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    Console.WriteLine($"Ping failed with client error {response.StatusCode} for service {_serviceName}. Check PingUrl  {MaskUrl(_currentConfig.Endpoint)}");
                    return;
                }

                Console.WriteLine($"Ping failed with server error {response.StatusCode} for service {_serviceName}. Will retry later.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine("Ping timed out");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, $"Ping request failed for service {_serviceName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ping request failed for service {_serviceName}");
            }
        }

        private async Task PingLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await LoadConfigurationFromCacheAsync();

                if (_currentConfig == null || !_currentConfig.HealthCheckEnabled || _timer == null)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                try
                {
                    await _timer.WaitForNextTickAsync(stoppingToken);
                    await PingAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Periodic ping failed for service {_currentConfig.ServiceName}");
                }
            }
        }

        private void ResetTimer()
        {
            _timer?.Dispose();

            if (_currentConfig == null || !_currentConfig.HealthCheckEnabled || _currentConfig.PingIntervalSeconds <= 0)
            {
                _timer = null;
                return;
            }

            _timer = new PeriodicTimer(TimeSpan.FromSeconds(_currentConfig.PingIntervalSeconds));
            _logger.LogInformation("[{ServiceName}] Timer set to {Interval}s", _serviceName, _currentConfig.PingIntervalSeconds);
        }

        private string MaskUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "[empty]";

            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath;
                if (path.Length > 20)
                {
                    return $"{uri.Scheme}://{uri.Host}/***{path.Substring(path.Length - 8)}";
                }
                return $"{uri.Scheme}://{uri.Host}/***";
            }
            catch
            {
                return "[masked]";
            }
        }
    }
}
