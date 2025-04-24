using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text.Json;

namespace Blocks.Genesis
{
    public sealed class RabbitMqService : IRabbitMqService, IAsyncDisposable
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

        public async Task CreateConnectionAsync()
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(_config.Connection),
                    VirtualHost = "/",
                    ContinuationTimeout = TimeSpan.FromSeconds(62),
                    AutomaticRecoveryEnabled = true
                };

                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync();

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
                foreach (var subscription in _config.RabbitMqConfiguration.ConsumerSubscriptions)
                {
                    await DeclareQueueAsync(subscription);

                    if (!string.IsNullOrWhiteSpace(subscription.ExchangeName))
                    {
                        await DeclareExchangeAsync(subscription);
                        await BindQueueToExchangeAsync(subscription);
                    }

                    await _channel.BasicQosAsync(0, subscription.PrefetchCount, global: false);
                    _logger.LogInformation("RabbitMQ subscription for -- {subscription}", JsonSerializer.Serialize(subscription));
                }

                _logger.LogInformation("RabbitMQ subscriptions initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize RabbitMQ subscriptions.");
                throw;
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
}
