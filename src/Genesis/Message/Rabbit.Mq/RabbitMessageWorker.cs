using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
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
    private readonly bool _enableTenantIsolation;
    private readonly ConcurrentDictionary<string, int> _queueConcurrencyLimits = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queueGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantGates = new();
    private readonly ConcurrentDictionary<string, string> _consumerTagToQueue = new();
    private readonly SemaphoreSlim _ackGate = new(1, 1);

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
        _enableTenantIsolation = _messageConfiguration?.RabbitMqConfiguration?.EnableTenantIsolation ?? false;
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
        var queueName = ResolveQueueName(ea);
        var maxConcurrency = _queueConcurrencyLimits.TryGetValue(queueName, out var configuredConcurrency)
            ? Math.Max(1, configuredConcurrency)
            : 1;

        if (maxConcurrency <= 1)
        {
            await ProcessMessageInternalAsync(ea);
            return;
        }

        if (_enableTenantIsolation)
        {
            var tenantId = GetHeader(ea.BasicProperties, "TenantId");
            var tenantKey = string.IsNullOrWhiteSpace(tenantId) ? "__unknown_tenant__" : tenantId;

            var queueGate = _queueGates.GetOrAdd(queueName, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));
            var tenantGate = _tenantGates.GetOrAdd(tenantKey, _ => new SemaphoreSlim(1, 1));

            await queueGate.WaitAsync();
            await tenantGate.WaitAsync();

            await ProcessMessageWithGatesAsync(ea, queueGate, tenantGate);
        }
        else
        {
            var gate = _queueGates.GetOrAdd(queueName, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));
            await gate.WaitAsync();
            await ProcessMessageWithGateAsync(ea, gate);
        }
    }

    private async Task ProcessMessageWithGateAsync(BasicDeliverEventArgs ea, SemaphoreSlim gate)
    {
        try
        {
            await ProcessMessageInternalAsync(ea);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ProcessMessageWithGatesAsync(BasicDeliverEventArgs ea, SemaphoreSlim queueGate, SemaphoreSlim tenantGate)
    {
        try
        {
            await ProcessMessageInternalAsync(ea);
        }
        finally
        {
            tenantGate.Release();
            queueGate.Release();
        }
    }

    private async Task ProcessMessageInternalAsync(BasicDeliverEventArgs ea)
    {
        ExtractHeaders(ea.BasicProperties, out var tenantId, out var traceId, out var spanId, out var securityContext, out var baggage);

        var processedSuccessfully = false;

        BlocksContext.SetContext(JsonSerializer.Deserialize<BlocksContext>(securityContext));
        var baggageItems = JsonSerializer.Deserialize<Dictionary<string, string>>(baggage ?? "{}") ?? new Dictionary<string, string>();
        foreach (var kvp in baggageItems)
        {
            Baggage.SetBaggage(kvp.Key, kvp.Value);
        }
        Baggage.SetBaggage("TenantId", tenantId);


        var parentContext = new ActivityContext(
            ActivityTraceId.CreateFromString(traceId),
            spanId != null ? ActivitySpanId.CreateFromString(spanId.AsSpan()) : ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            traceState: null,
            isRemote: true
        );

        using var activity = _activitySource.StartActivity("process.messaging.rabbitmq", ActivityKind.Consumer, parentContext);

        activity?.SetTag("SecurityContext", securityContext);
        activity?.SetTag("messaging.destination.name", ea.RoutingKey);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("usage", true);


        var body = ea.Body.ToArray();
        _logger.LogInformation("Received message: {Body}", Encoding.UTF8.GetString(body));
        activity?.SetTag("message.body", Encoding.UTF8.GetString(body));

        try
        {
            var message = JsonSerializer.Deserialize<Message>(body);

            if (message != null)
            {
                await _consumer.ProcessMessageAsync(message.Type, message.Body);
                processedSuccessfully = true;
                activity?.SetTag("response", "Successfully Completed");
                activity?.SetStatus(ActivityStatusCode.Ok, "Message processed successfully");
                _logger.LogInformation("Message processed successfully.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing message.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error", ex.Message);
        }
        finally
        {
            if (_channel?.IsOpen == true)
            {
                var deliveryCount = GetDeliveryCount(ea.BasicProperties);
                var maxRetryCount = _messageConfiguration?.RabbitMqConfiguration?.MaxRetryCount ?? 3;

                await _ackGate.WaitAsync();
                try
                {
                    if (processedSuccessfully)
                    {
                        _logger.LogInformation("Ack message {DeliveryTag}. Reason: processed successfully.", ea.DeliveryTag);
                        await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    else
                    {
                        var shouldSendToDeadZone = maxRetryCount > 0 && deliveryCount >= maxRetryCount;
                        if (shouldSendToDeadZone)
                        {
                            var deadLetterExchange = _messageConfiguration?.RabbitMqConfiguration?.DeadLetterExchange;
                            if (!string.IsNullOrWhiteSpace(deadLetterExchange))
                            {
                                // requeue:false lets broker route to DLX/dead zone when configured.
                                _logger.LogWarning("Nack message {DeliveryTag} with requeue=false. Reason: retry limit reached ({DeliveryCount}/{MaxRetryCount}); dead-zone route active via exchange {DeadLetterExchange}.",
                                    ea.DeliveryTag,
                                    deliveryCount,
                                    maxRetryCount,
                                    deadLetterExchange);
                                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                            }
                            else
                            {
                                _logger.LogWarning("Ack message {DeliveryTag}. Reason: retry limit reached ({DeliveryCount}/{MaxRetryCount}) and dead-zone is not configured; fallback ack to avoid infinite retry loop.",
                                    ea.DeliveryTag,
                                    deliveryCount,
                                    maxRetryCount);
                                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Nack message {DeliveryTag} with requeue=true. Reason: transient failure, retry attempt {DeliveryCount}/{MaxRetryCount}.",
                                ea.DeliveryTag,
                                deliveryCount,
                                maxRetryCount);
                            await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                        }
                    }
                }
                finally
                {
                    _ackGate.Release();
                }
            }

            activity?.Stop();
            BlocksContext.ClearContext();
        }
    }

    private async Task StartConsumingAsync(AsyncEventingBasicConsumer consumer)
    {
        foreach (var subscription in _messageConfiguration?.RabbitMqConfiguration?.ConsumerSubscriptions ?? new())
        {
            _queueConcurrencyLimits[subscription.QueueName] = Math.Max(1, subscription.MaxWorkerConcurrency);
            var consumerTag = await _channel!.BasicConsumeAsync(subscription.QueueName, autoAck: false, consumer);
            _consumerTagToQueue[consumerTag] = subscription.QueueName;
            _logger.LogInformation("Started consuming queue: {QueueName}, PrefetchCount: {PrefetchCount}, MaxWorkerConcurrency: {MaxWorkerConcurrency}, TenantIsolation: {TenantIsolation}",
                subscription.QueueName,
                subscription.PrefetchCount,
                subscription.MaxWorkerConcurrency,
                _enableTenantIsolation);
        }
    }

    private string ResolveQueueName(BasicDeliverEventArgs ea)
    {
        if (!string.IsNullOrWhiteSpace(ea.ConsumerTag) && _consumerTagToQueue.TryGetValue(ea.ConsumerTag, out var queueName))
        {
            return queueName;
        }

        // Fallback for tests and edge cases where consumer tag mapping is unavailable.
        return ea.RoutingKey;
    }

    private static int GetDeliveryCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers == null)
        {
            return 0;
        }

        if (properties.Headers.TryGetValue("x-delivery-count", out var deliveryCountValue))
        {
            return ToInt(deliveryCountValue);
        }

        if (properties.Headers.TryGetValue("x-death", out var deathValue) && deathValue is IList<object> deaths && deaths.Count > 0)
        {
            var total = 0;
            foreach (var death in deaths)
            {
                if (death is IDictionary<string, object> deathDict && deathDict.TryGetValue("count", out var countValue))
                {
                    total += ToInt(countValue);
                }
            }

            return total;
        }

        return 0;
    }

    private static int ToInt(object? value)
    {
        return value switch
        {
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => (int)ui,
            long l => (int)l,
            ulong ul => (int)ul,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => 0
        };
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
            return value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
                string str => str,
                _ => value?.ToString()
            };
        }
        return null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel?.IsOpen == true)
        {
            await _channel.CloseAsync();
            await _channel.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("RabbitMessageWorker has been stopped at {Time}", DateTimeOffset.Now);
    }
}
