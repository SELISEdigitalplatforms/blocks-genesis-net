using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis.Health;

/// <summary>
/// Background service that periodically pings a configured health endpoint.
/// Configuration is loaded from MongoDB and cached in Redis.
/// Supports dynamic config refresh and exponential backoff on failure.
/// </summary>
public sealed class GenesisHealthPingBackgroundService : BackgroundService
{
    private readonly ILogger<GenesisHealthPingBackgroundService> _logger;
    private readonly IDbContextProvider _dbContextProvider;
    private readonly IDatabase _cacheDb;
    private readonly HttpClient _httpClient;
    private readonly IBlocksSecret _blocksSecret;

    private readonly string _serviceName;
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly string _configKey;

    private const string CollectionName = "BlocksServicesHealthConfigurations";
    private const int CacheExpirationMinutes = 15;
    private const int DefaultPingIntervalSecs = 60;
    private const int MaxBackoffSeconds = 300;

    private static readonly TimeSpan ConfigRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromHours(1);

    private volatile BlocksServicesHealthConfiguration? _currentConfig;

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

        // SocketsHttpHandler with PooledConnectionLifetime forces periodic DNS
        // re-resolution, preventing stale IP issues when endpoints change.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Genesis.HealthPing/1.0");
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[{Service}] Health ping worker starting", _serviceName);

        // Small startup delay so the host finishes wiring before we hit the DB.
        await Task.Delay(StartupDelay, stoppingToken);

        var nextConfigRefresh = DateTimeOffset.UtcNow;
        var failureCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // -----------------------------------------------------------------
                // 1. Refresh config on schedule
                // -----------------------------------------------------------------
                if (DateTimeOffset.UtcNow >= nextConfigRefresh)
                {
                    await RefreshConfigurationAsync(stoppingToken);
                    nextConfigRefresh = DateTimeOffset.UtcNow.Add(ConfigRefreshInterval);
                }

                var config = _currentConfig;

                // -----------------------------------------------------------------
                // 2. If disabled / not found, wait and retry — never exit the loop.
                //    This allows the service to self-activate when config is added.
                // -----------------------------------------------------------------
                if (config is null)
                {
                    Console.WriteLine($"[{_serviceName}] No configuration found, retrying in {DisabledPollInterval}");
                    await Task.Delay(DisabledPollInterval, stoppingToken);
                    continue;
                }

                if (!config.HealthCheckEnabled)
                {
                    Console.WriteLine($"[{_serviceName}] Health check disabled, retrying in {DisabledPollInterval}");
                    await Task.Delay(DisabledPollInterval, stoppingToken);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(config.Endpoint))
                {
                    Console.WriteLine($"[{_serviceName}] Endpoint is empty — skipping ping");
                    await Task.Delay(DisabledPollInterval, stoppingToken);
                    continue;
                }

                // -----------------------------------------------------------------
                // 3. Ping
                // -----------------------------------------------------------------
                var success = await PingAsync(config, stoppingToken);

                failureCount = success ? 0 : failureCount + 1;

                // -----------------------------------------------------------------
                // 4. Wait before next ping (exponential backoff on failure)
                // -----------------------------------------------------------------
                var delay = CalculateDelay(config.PingIntervalSeconds, failureCount);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Service}] Unexpected error in health ping loop", _serviceName);
                await Task.Delay(DisabledPollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("[{Service}] Health ping worker stopped", _serviceName);
    }

    // =========================================================================
    // Configuration
    // =========================================================================

    /// <summary>
    /// Tries Redis first, falls back to MongoDB, and writes back to Redis on a miss.
    /// </summary>
    private async Task RefreshConfigurationAsync(CancellationToken ct)
    {
        try
        {
            // --- Try cache ---
            var cached = await _cacheDb.StringGetAsync(_configKey);

            if (cached.HasValue)
            {
                var cachedConfig = JsonSerializer.Deserialize<BlocksServicesHealthConfiguration>((string)cached!);

                if (cachedConfig is not null)
                {
                    ApplyConfig(cachedConfig);
                    return;
                }
            }

            // --- Fall back to DB ---
            await LoadConfigurationFromDatabaseAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Service}] Config refresh failed — continuing with last known config", _serviceName);
        }
    }

    private async Task LoadConfigurationFromDatabaseAsync(CancellationToken ct)
    {
        var database = _dbContextProvider.GetDatabase(_connectionString, _databaseName);
        var collection = database.GetCollection<BlocksServicesHealthConfiguration>(CollectionName);
        var filter = Builders<BlocksServicesHealthConfiguration>.Filter.Eq(x => x.ServiceName, _serviceName);

        var config = await collection.Find(filter).FirstOrDefaultAsync(ct);

        if (config is null)
        {
            _logger.LogWarning("[{Service}] No health configuration found in database", _serviceName);
            return;
        }

        LogConfigChanges(config);
        ApplyConfig(config);

        // Write-through to Redis
        try
        {
            await _cacheDb.StringSetAsync(
                _configKey,
                JsonSerializer.Serialize(config),
                TimeSpan.FromMinutes(CacheExpirationMinutes));
        }
        catch (Exception cacheEx)
        {
            _logger.LogWarning(cacheEx, "[{Service}] Failed to write config to cache — using in-memory config", _serviceName);
        }
    }

    /// <summary>
    /// Logs only when meaningful fields have actually changed.
    /// </summary>
    private void LogConfigChanges(BlocksServicesHealthConfiguration incoming)
    {
        var current = _currentConfig;

        if (current is null ||
            current.HealthCheckEnabled != incoming.HealthCheckEnabled ||
            current.Endpoint != incoming.Endpoint ||
            current.PingIntervalSeconds != incoming.PingIntervalSeconds)
        {
            _logger.LogInformation(
                "[{Service}] Configuration updated — Enabled={Enabled}, Interval={Interval}s, Endpoint={Endpoint}",
                _serviceName,
                incoming.HealthCheckEnabled,
                incoming.PingIntervalSeconds,
                MaskUrl(incoming.Endpoint));
        }
    }

    private void ApplyConfig(BlocksServicesHealthConfiguration config)
    {
        _currentConfig = config;
    }

    // =========================================================================
    // Pinging
    // =========================================================================
    private async Task<bool> PingAsync(BlocksServicesHealthConfiguration config, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(config.Endpoint, ct);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[{_serviceName}] Ping succeeded — Status={response.StatusCode}, Endpoint={MaskUrl(config.Endpoint)}");
                return true;
            }

            var statusCode = (int)response.StatusCode;

            // 4xx → configuration/auth problem; log as warning (no point in loud retries)
            // 5xx → transient server error; log as error (backoff will kick in)
            if (statusCode is >= 400 and < 500)
            {
                _logger.LogWarning(
                    "[{Service}] Ping returned client error {Status} — check endpoint config: {Endpoint}",
                    _serviceName,
                    statusCode,
                    MaskUrl(config.Endpoint));
            }
            else
            {
                _logger.LogError(
                    "[{Service}] Ping returned server error {Status} for {Endpoint}",
                    _serviceName,
                    statusCode,
                    MaskUrl(config.Endpoint));
            }

            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[{Service}] Ping timed out for {Endpoint}", _serviceName, MaskUrl(config.Endpoint));
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{Service}] Ping network error for {Endpoint}", _serviceName, MaskUrl(config.Endpoint));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Service}] Ping unexpected error for {Endpoint}", _serviceName, MaskUrl(config.Endpoint));
            return false;
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Returns the normal interval on success.
    /// On failure, applies exponential backoff (base = configured interval) capped at MaxBackoffSeconds.
    /// </summary>
    private static TimeSpan CalculateDelay(int intervalSeconds, int failureCount)
    {
        if (intervalSeconds <= 0)
            intervalSeconds = DefaultPingIntervalSecs;

        if (failureCount == 0)
            return TimeSpan.FromSeconds(intervalSeconds);

        // Scale backoff from the configured interval, not from 1s
        var backoffSeconds = Math.Min(intervalSeconds * Math.Pow(2, failureCount), MaxBackoffSeconds);
        return TimeSpan.FromSeconds(backoffSeconds);
    }

    /// <summary>
    /// Returns a URL with the path replaced by *** to avoid leaking tokens/keys in logs.
    /// </summary>
    private static string MaskUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "[empty]";

        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;

            // Keep the last 8 chars of the path as a breadcrumb, hide the rest.
            var suffix = path.Length > 8 ? path[^8..] : path;
            return $"{uri.Scheme}://{uri.Host}/***{suffix}";
        }
        catch
        {
            return "[masked]";
        }
    }
}