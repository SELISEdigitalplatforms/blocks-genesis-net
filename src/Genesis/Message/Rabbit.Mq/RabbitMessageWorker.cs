using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        if (subscription?.ParallelProcessing ?? false)
        {
            _ = Task.Run(() => ProcessMessageInternalAsync(ea));
        } else
        {
            await ProcessMessageInternalAsync(ea);
        }   
    }

    private async Task ProcessMessageInternalAsync(BasicDeliverEventArgs ea)
    {
        ExtractHeaders(ea.BasicProperties, out var tenantId, out var traceId, out var spanId, out var securityContext);

        BlocksContext.SetContext(JsonSerializer.Deserialize<BlocksContext>(securityContext));

        var parentContext = new ActivityContext(
            ActivityTraceId.CreateFromString(traceId),
            spanId != null ? ActivitySpanId.CreateFromString(spanId.AsSpan()) : ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            traceState: null,
            isRemote: true
        );

        using var activity = _activitySource.StartActivity("process.messaging.rabbitmq", ActivityKind.Consumer, parentContext);
        activity?.SetTag("TenantId", tenantId);
        activity?.SetTag("SecurityContext", securityContext);

        var body = ea.Body.ToArray();
        _logger.LogInformation("Received message: {Body}", Encoding.UTF8.GetString(body));
        activity?.SetTag("MessageBody", Encoding.UTF8.GetString(body));

        try
        {
            var message = JsonSerializer.Deserialize<Message>(body);

            if (message != null)
            {
                await _consumer.ProcessMessageAsync(message.Type, message.Body);
                activity?.SetTag("ProcessingResult", "Success");
                _logger.LogInformation("Message processed successfully.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing message.");
            activity?.SetTag("ProcessingResult", "Error");
            activity?.SetTag("ErrorMessage", ex.Message);
        }
        finally
        {
            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
            activity?.Stop();
        }

        BlocksContext.ClearContext();
    }

    private async Task StartConsumingAsync(AsyncEventingBasicConsumer consumer)
    {
        foreach (var subscription in _messageConfiguration?.RabbitMqConfiguration?.ConsumerSubscriptions ?? new())
        {
            await _channel!.BasicConsumeAsync(subscription.QueueName, autoAck: false, consumer);
            _logger.LogInformation("Started consuming queue: {QueueName}, Parallel: {Parallel}, MaxConcurrency: {MaxConcurrency}",
            subscription.QueueName,
            subscription.ParallelProcessing,
            subscription.PrefetchCount);
        }
    }

    private static void ExtractHeaders(IReadOnlyBasicProperties properties, out string? tenantId, out string? traceId, out string? spanId, out string? securityContext)
    {
        tenantId = GetHeader(properties, "TenantId");
        traceId = GetHeader(properties, "TraceId");
        spanId = GetHeader(properties, "SpanId");
        securityContext = GetHeader(properties, "SecurityContext");
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
        if (_channel?.IsOpen == true)
        {
            await _channel.CloseAsync();
            await _channel.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("RabbitMessageWorker has been stopped at {Time}", DateTimeOffset.Now);
    }
}
