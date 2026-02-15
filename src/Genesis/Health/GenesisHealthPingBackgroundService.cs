using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Blocks.Genesis.Health
{
    public class GenesisHealthPingBackgroundService: BackgroundService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GenesisHealthPingBackgroundService> _logger;
        private readonly IDbContextProvider _dbContextProvider;
        private readonly IBlocksSecret _blocksSecret;
        private readonly string _serviceName;
        private readonly string _connectionString;
        private readonly string _databaseName;


        private PeriodicTimer? _timer;
        private IBlocksServicesHealthConfiguration? _currentConfig;

        public GenesisHealthPingBackgroundService(
            IHttpClientFactory httpClientFactory,
            ILogger<GenesisHealthPingBackgroundService> logger,
            IDbContextProvider dbContextProvider,
            IBlocksSecret blocksSecret)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _dbContextProvider = dbContextProvider;
            _blocksSecret = blocksSecret;
            _serviceName = _blocksSecret.ServiceName;
            _connectionString = _blocksSecret.DatabaseConnectionString;
            _databaseName = _blocksSecret.RootDatabaseName;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) 
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            // Load initial configuration from database
            await LoadConfigurationFromDatabaseAsync();

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
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);

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

        private async Task LoadConfigurationFromDatabaseAsync()
        {
            try
            {
                var database = _dbContextProvider.GetDatabase(_connectionString,_databaseName);
                var genesisHealthConfigurationCollection = database.GetCollection<BlocksServicesHealthConfiguration>("BlocksServicesHealthConfigurations");

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
            try
            {
                var client = _httpClientFactory.CreateClient("NoLogging");
                using var response = await client.GetAsync(_currentConfig.Endpoint, ct);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Ping success ({response.StatusCode}) for url {MaskUrl(_currentConfig.Endpoint)}");
                    return;
                }

                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    Console.WriteLine($"Ping failed with client error {response.StatusCode}. Check PingUrl  {MaskUrl(_currentConfig.Endpoint)}");
                    return;
                }

                Console.WriteLine($"Ping failed with server error {response.StatusCode}. Will retry later.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine("Ping timed out");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Ping request failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ping request failed");
            }
        }

        private async Task PingLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_currentConfig.HealthCheckEnabled || _timer == null)
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
                    _logger.LogError(ex, "Periodic ping failed");
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
