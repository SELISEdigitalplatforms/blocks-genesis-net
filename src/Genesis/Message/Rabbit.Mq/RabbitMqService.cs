using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text.Json;

namespace Blocks.Genesis;

public sealed class RabbitMqService : IRabbitMqService, IDisposable, IAsyncDisposable
{
    private readonly ILogger<RabbitMqService> _logger;
    private readonly MessageConfiguration _config;

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;


    public IChannel RabbitMqChannel => _channel ?? throw new InvalidOperationException("Channel has not been initialized.");


    public RabbitMqService(ILogger<RabbitMqService> logger, MessageConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>Total start-up wait for this service; null reads Startup__DependencyWaitSeconds (default 120). Tests set it.</summary>
    internal TimeSpan? DependencyWait { get; set; }

    /// <summary>
    /// Connects and opens the channel, retrying while the broker is not reachable (exponential backoff, 1 s to 30 s,
    /// within the Startup__DependencyWaitSeconds budget). Returns only when the channel is open; when the budget is
    /// used up, or the connection string is invalid, the failure is thrown, so the channel is never used uninitialised.
    /// </summary>
    public async Task CreateConnectionAsync()
    {
        try
        {
            await StartupRetry.ExecuteAsync("RabbitMQ", async cancellationToken =>
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(_config.Connection!),
                    VirtualHost = "/",
                    ContinuationTimeout = TimeSpan.FromSeconds(62),
                    AutomaticRecoveryEnabled = true
                };

                var connection = await factory.CreateConnectionAsync(cancellationToken);
                try
                {
                    _channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
                    _connection = connection;
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }
            }, _logger, ConfigureRabbitMq.IsTransient, DependencyWait);

            _logger.LogInformation("Successfully established RabbitMQ connection and channel.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while creating the RabbitMQ connection.");
            throw;
        }
    }

    public async Task InitializeSubscriptionsAsync()
    {
        if (_channel == null)
            throw new InvalidOperationException("RabbitMQ channel is not initialized.");

        try
        {
            foreach (var subscription in _config.RabbitMqConfiguration!.ConsumerSubscriptions)
            {
                await DeclareQueueAsync(subscription);

                if (!string.IsNullOrWhiteSpace(subscription.ExchangeName))
                {
                    await DeclareExchangeAsync(subscription);
                    await BindQueueToExchangeAsync(subscription);
                }

                await _channel.BasicQosAsync(0, subscription.PrefetchCount, global: false);
                _logger.LogInformation("RabbitMQ subscription for -- {Subscription}", JsonSerializer.Serialize(subscription));
            }

            _logger.LogInformation("RabbitMQ subscriptions initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RabbitMQ subscriptions.");
        }
    }

    private async Task DeclareQueueAsync(ConsumerSubscription subscription)
    {
        await _channel!.QueueDeclareAsync(
            queue: subscription.QueueName,
            durable: subscription.Durable,
            exclusive: false,
            autoDelete: false,
            arguments: null);
    }

    private async Task DeclareExchangeAsync(ConsumerSubscription subscription)
    {
        await _channel!.ExchangeDeclareAsync(
            exchange: subscription.ExchangeName,
            type: subscription.ExchangeType,
            durable: subscription.Durable,
            autoDelete: false,
            arguments: null);
    }

    private async Task BindQueueToExchangeAsync(ConsumerSubscription subscription)
    {
        await _channel!.QueueBindAsync(
            queue: subscription.QueueName,
            exchange: subscription.ExchangeName,
            routingKey: subscription.RoutingKey,
            arguments: null);
    }


    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            if (_channel?.IsOpen == true)
            {
                await _channel.CloseAsync();
                await _channel.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An error occurred while closing the RabbitMQ channel.");
        }

        try
        {
            if (_connection?.IsOpen == true)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An error occurred while closing the RabbitMQ connection.");
        }
    }


    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
