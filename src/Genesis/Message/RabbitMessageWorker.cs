using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

public class RabbitMessageWorker : BackgroundService
{
    private readonly ILogger<RabbitMessageWorker> _logger;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly Consumer _consumer;
    private readonly ActivitySource _activitySource;

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMessageWorker(
        ILogger<RabbitMessageWorker> logger,
        MessageConfiguration messageConfiguration,
        Consumer consumer,
        ActivitySource activitySource)
    {
        _logger = logger;
        _messageConfiguration = messageConfiguration;
        _consumer = consumer;
        _activitySource = activitySource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync();

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (ch, ea) =>
        {
            ExtractHeaders(ea.BasicProperties, out var tenantId, out var traceId, out var spanId, out var securityContextString);

            var parentActivityContext = new ActivityContext(
                ActivityTraceId.CreateFromString(traceId),
                spanId != null ? ActivitySpanId.CreateFromString(spanId.AsSpan()) : ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded,
                traceState: null,  // Set the traceState from the incoming message
                isRemote: true
            );

            using var activity = _activitySource.StartActivity("ProcessMessage", ActivityKind.Consumer, parentActivityContext);

            activity?.SetCustomProperty("SecurityContext", securityContextString);
            activity.SetCustomProperty("TenantId", tenantId);

            var body = ea.Body.ToArray();
            _logger.LogInformation($"Message received: {body}");
            activity.SetCustomProperty("Request", body);
            activity?.SetTag("message", body);

            try
            {
                var message = JsonSerializer.Deserialize<Message>(body);

                if (message != null)
                {
                    await _consumer.ProcessMessageAsync(message.Type, message.Body);

                    _logger.LogInformation("Message processed and acknowledged.");
                    activity.SetCustomProperty("Response", "Successfully Completed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing RabbitMQ message.");
                activity.SetCustomProperty("Response", ex);
            }
            finally
            {
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
                activity?.Stop();
                _logger.LogInformation($"Message processing time: {activity?.Duration} ms");
            }
        };

        foreach (var queue in _messageConfiguration.Queues)
        {
            await _channel!.BasicConsumeAsync(queue, autoAck: false, consumer: consumer);
            _logger.LogInformation("Started consumer for queue: {Queue}", queue);
        }

        foreach (var queue in _messageConfiguration.Topics)
        {
            await _channel!.BasicConsumeAsync(queue, autoAck: false, consumer: consumer);
            _logger.LogInformation("Started consumer for queue: {Queue}", queue);
        }

        _logger.LogInformation("Consumer started. Waiting for messages...");
        await Task.CompletedTask;
    }

    private void ExtractHeaders(IReadOnlyBasicProperties properties, out string? tenantId, out string? traceId, out string? spanId, out string? securityContextString)
    {
        tenantId = null;
        traceId = null;
        spanId = null;
        securityContextString = null;

        if (properties.Headers == null) return;

        if (properties.Headers.TryGetValue("TenantId", out var tenantIdObj))
            tenantId = Encoding.UTF8.GetString((byte[])tenantIdObj);

        if (properties.Headers.TryGetValue("TraceId", out var traceIdObj))
            traceId = Encoding.UTF8.GetString((byte[])traceIdObj);

        if (properties.Headers.TryGetValue("SpanId", out var spanIdObj))
            spanId = Encoding.UTF8.GetString((byte[])spanIdObj);

        if (properties.Headers.TryGetValue("SecurityContext", out var contextObj))
        {
            securityContextString = Encoding.UTF8.GetString((byte[])contextObj);
            
        }
    }


    private async Task InitializeAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_messageConfiguration.Connection),
                VirtualHost = "/",
                ContinuationTimeout = TimeSpan.FromSeconds(30),
                AutomaticRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync();
            await InitializeChannelsAsync();
            _logger.LogInformation("RabbitMQ channel and connection established.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RabbitMQ.");
            throw;
        }
    }

    private async Task InitializeChannelsAsync()
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connection is not initialized");
        }

        try
        {
            _channel = await _connection.CreateChannelAsync();

            // Initialize queues
            foreach (var queue in _messageConfiguration.Queues)
            {
                await _channel.QueueDeclareAsync(
                    queue: queue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);
                _logger.LogDebug("Queue {QueueName} initialized", queue);
            }

            // Initialize exchanges and store them in the HashSet
            foreach (var exchange in _messageConfiguration.Topics)
            {
                await _channel.ExchangeDeclareAsync(
                    exchange: exchange,
                    type: ExchangeType.Fanout,
                    durable: true,
                    autoDelete: false,
                    arguments: null);
                _logger.LogDebug("Exchange {ExchangeName} initialized", exchange);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize channels");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
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

            if (_connection?.IsOpen == true)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping RabbitMQ worker.");
        }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("RabbitMQ worker stopped at: {time}", DateTimeOffset.Now);
    }
}
