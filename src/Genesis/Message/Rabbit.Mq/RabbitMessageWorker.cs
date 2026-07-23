using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

public sealed class RabbitMessageWorker : BackgroundService
{
    private readonly ILogger<RabbitMessageWorker> _logger;
    private readonly MessageConfiguration _messageConfiguration;
    private readonly IRabbitMqService _rabbitMqService;
    private readonly Consumer _consumer;
    private readonly ActivitySource _activitySource;

    private IChannel? _channel;

    public RabbitMessageWorker(
        ILogger<RabbitMessageWorker> logger,
        MessageConfiguration messageConfiguration,
        IRabbitMqService rabbitMqService,
        Consumer consumer,
        ActivitySource activitySource)
    {
        _logger = logger;
        _messageConfiguration = messageConfiguration;
        _rabbitMqService = rabbitMqService;
        _consumer = consumer;
        _activitySource = activitySource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeRabbitMqAsync();

        if (_channel == null)
        {
            throw new InvalidOperationException("Channel is not initialized");
        }


        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += HandleMessageAsync;

        await StartConsumingAsync(consumer);

        _logger.LogInformation("RabbitMQ consumer is running and awaiting messages.");
    }

    private async Task InitializeRabbitMqAsync()
    {
        await _rabbitMqService.CreateConnectionAsync();
        await _rabbitMqService.InitializeSubscriptionsAsync();
        _channel = _rabbitMqService.RabbitMqChannel;
    }

    private async Task HandleMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        var subscription = _messageConfiguration.RabbitMqConfiguration.ConsumerSubscriptions
            .FirstOrDefault(s => s.QueueName == ea.RoutingKey);

        try
        {
            if (subscription?.ParallelProcessing ?? false)
            {
                _ = ProcessMessageInternalAsync(ea).ContinueWith(async t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "Error in parallel message processing for queue {Queue}", ea.RoutingKey);
                        try
                        {
                            if (_channel?.IsOpen == true)
                                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                        }
                        catch (Exception nackEx)
                        {
                            _logger.LogError(nackEx, "Failed to NACK message after parallel processing error");
                        }
                    }
                }, TaskScheduler.Default).Unwrap();
            }
            else
            {
                await ProcessMessageInternalAsync(ea);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error in message handler for queue {Queue}. Message will be NACKed.", ea.RoutingKey);
            try
            {
                if (_channel?.IsOpen == true)
                    await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            }
            catch (Exception nackEx)
            {
                _logger.LogError(nackEx, "Failed to NACK message after critical error");
            }
        }
    }

    private async Task ProcessMessageInternalAsync(BasicDeliverEventArgs ea)
    {
        try
        {
            ExtractHeaders(ea.BasicProperties, out var tenantId, out var traceId, out var spanId, out var securityContext, out var baggage);

            try
            {
                BlocksContext.SetContext(JsonSerializer.Deserialize<BlocksContext>(securityContext));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                BlocksContext.SetContext(null);
            }

            try
            {
                foreach (var kvp in JsonSerializer.Deserialize<Dictionary<string, string>>(baggage ?? "{}") ?? [])
                {
                    Baggage.SetBaggage(kvp.Key, kvp.Value);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize baggage, skipping baggage context");
            }
            Baggage.SetBaggage("TenantId", tenantId);

            ActivityContext parentContext;
            try
            {
                parentContext = new ActivityContext(
                    ActivityTraceId.CreateFromString(traceId ?? ActivityTraceId.CreateRandom().ToString()),
                    spanId != null ? ActivitySpanId.CreateFromString(spanId.AsSpan()) : ActivitySpanId.CreateRandom(),
                    ActivityTraceFlags.Recorded,
                    traceState: null,
                    isRemote: true
                );
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid trace context, creating new context");
                parentContext = new ActivityContext(
                    ActivityTraceId.CreateRandom(),
                    ActivitySpanId.CreateRandom(),
                    ActivityTraceFlags.Recorded,
                    traceState: null,
                    isRemote: false
                );
            }

            using var activity = _activitySource.StartActivity("process.messaging.rabbitmq", ActivityKind.Consumer, parentContext);

            activity?.SetTag("SecurityContext", securityContext);
            activity?.SetTag("messaging.destination.name", ea.RoutingKey);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("usage", true);


            var body = ea.Body.ToArray();
            _logger.LogInformation("Received message for queue {Queue}", ea.RoutingKey);

            var processedSuccessfully = false;
            try
            {
                var message = JsonSerializer.Deserialize<Message>(body);

                if (message != null)
                {
                    await _consumer.ProcessMessageAsync(message.Type, message.Body);
                    activity?.SetTag("response", "Successfully Completed");
                    activity?.SetStatus(ActivityStatusCode.Ok, "Message processed successfully");
                    _logger.LogInformation("Message processed successfully.");
                    processedSuccessfully = true;
                }
                else
                {
                    _logger.LogWarning("Received empty or invalid message envelope for queue {Queue}. Message will be acknowledged.", ea.RoutingKey);
                    processedSuccessfully = true;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON payload received for queue {Queue}. Message will be acknowledged.", ea.RoutingKey);
                activity?.SetStatus(ActivityStatusCode.Error, "Invalid JSON payload");
                processedSuccessfully = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing message.");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error", ex.Message);
            }
            finally
            {
                if (processedSuccessfully)
                {
                    if (_channel?.IsOpen == true)
                        await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                }
                else
                {
                    if (_channel?.IsOpen == true)
                        await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                }
                activity?.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in message processing for queue {Queue}", ea.RoutingKey);
            try
            {
                if (_channel?.IsOpen == true)
                    await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            }
            catch (Exception nackEx)
            {
                _logger.LogError(nackEx, "Failed to NACK message after unexpected error");
            }
        }
        finally
        {
            try
            {
                BlocksContext.ClearContext();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error clearing BlocksContext");
            }
        }
    }

    private async Task StartConsumingAsync(AsyncEventingBasicConsumer consumer)
    {
        foreach (var subscription in _messageConfiguration?.RabbitMqConfiguration?.ConsumerSubscriptions ?? [])
        {
            await _channel!.BasicConsumeAsync(subscription.QueueName, autoAck: false, consumer);
            _logger.LogInformation("Started consuming queue: {QueueName}, Parallel: {Parallel}, MaxConcurrency: {MaxConcurrency}",
            subscription.QueueName,
            subscription.ParallelProcessing,
            subscription.PrefetchCount);
        }
    }

    private static void ExtractHeaders(IReadOnlyBasicProperties properties, out string? tenantId, out string? traceId, out string? spanId, out string? securityContext, out string baggage)
    {
        tenantId = GetHeader(properties, "TenantId");
        traceId = GetHeader(properties, "TraceId");
        spanId = GetHeader(properties, "SpanId");
        securityContext = GetHeader(properties, "SecurityContext");
        baggage = GetHeader(properties, "Baggage");
    }

    private static string? GetHeader(IReadOnlyBasicProperties properties, string key)
    {
        if (properties.Headers != null && properties.Headers.TryGetValue(key, out var value))
        {
            return Encoding.UTF8.GetString((byte[])value);
        }
        return null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_channel?.IsOpen == true)
            {
                try
                {
                    await _channel.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing channel");
                }
                
                try
                {
                    await _channel.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing channel");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during channel shutdown");
        }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("RabbitMessageWorker has been stopped at {Time}", DateTimeOffset.Now);
    }
}
