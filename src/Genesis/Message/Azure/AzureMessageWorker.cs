using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public sealed class AzureMessageWorker : BackgroundService
    {
        private readonly ILogger<AzureMessageWorker> _logger;
        private readonly List<ServiceBusProcessor> _processors = new List<ServiceBusProcessor>();
        private readonly MessageConfiguration _messageConfiguration;
        private readonly ActivitySource _activitySource;
        private ServiceBusClient _serviceBusClient;
        private readonly Consumer _consumer;

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeMessageRenewals = new ConcurrentDictionary<string, CancellationTokenSource>();
        public event EventHandler<AutoRenewalEventArgs> MessageProcessingStarted;

        public AzureMessageWorker(ILogger<AzureMessageWorker> logger, MessageConfiguration messageConfiguration, Consumer consumer, ActivitySource activitySource)
        {
            _logger = logger;
            _messageConfiguration = messageConfiguration;
            _consumer = consumer;
            _activitySource = activitySource;

            Initialization();
        }

        private void Initialization()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_messageConfiguration.Connection))
                {
                    _logger.LogError("Connection string missing");
                    return;
                }

                _serviceBusClient = new ServiceBusClient(_messageConfiguration.Connection);
                _logger.LogInformation("Service Bus Client initialized at: {Time}", DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during initialization");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Cancel all active message lock renewals
            foreach (var renewalPair in _activeMessageRenewals)
            {
               await renewalPair.Value.CancelAsync();
            }
            _activeMessageRenewals.Clear();

            foreach (var processor in _processors)
            {
                try
                {
                    await processor.StopProcessingAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping processor");
                }
                finally
                {
                    await processor.DisposeAsync();
                }
            }

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("Worker stopped at: {Time}", DateTimeOffset.Now);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_serviceBusClient == null)
                {
                    _logger.LogError("Service Bus Client is not initialized");
                    throw new InvalidOperationException("Service Bus Client is not initialized");
                }

                MessageProcessingStarted += async (sender, e) =>
                {
                   await StartAutoRenewalTask(e.Args, e.Token);
                };

                var queueProcessingTask = ProcessQueues(stoppingToken);
                var topicesProcessingTask = ProcessTopics(stoppingToken);

                await Task.WhenAll(queueProcessingTask, topicesProcessingTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ExecuteAsync");
            }
        }

        private async Task ProcessQueues(CancellationToken stoppingToken)
        {
            foreach (var queueName in _messageConfiguration?.AzureServiceBusConfiguration?.Queues ?? new())
            {
                var processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
                {
                    PrefetchCount = _messageConfiguration?.AzureServiceBusConfiguration?.QueuePrefetchCount ?? 10,
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = _messageConfiguration?.AzureServiceBusConfiguration?.MaxConcurrentCalls ?? 5
                });
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(stoppingToken);
            }
        }

        private async Task ProcessTopics(CancellationToken stoppingToken)
        {
            foreach (var topicName in _messageConfiguration?.AzureServiceBusConfiguration?.Topics ?? new())
            {
                var processor = _serviceBusClient.CreateProcessor(topicName, _messageConfiguration?.GetSubscriptionName(topicName), new ServiceBusProcessorOptions
                {
                    PrefetchCount = _messageConfiguration?.AzureServiceBusConfiguration?.TopicPrefetchCount ?? 10,
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = _messageConfiguration?.AzureServiceBusConfiguration?.MaxConcurrentCalls ?? 5
                });
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(stoppingToken);
            }
        }

        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            // Extract trace context from the message
            var traceId = args.Message.ApplicationProperties.TryGetValue("TraceId", out var traceIdObj) ? traceIdObj.ToString() : "";
            var spanId = args.Message.ApplicationProperties.TryGetValue("SpanId", out var spanIdObj) ? spanIdObj.ToString() : "";
            var tenantId = args.Message.ApplicationProperties.TryGetValue("TenantId", out var tenantIdObj) ? tenantIdObj.ToString() : "";
            var securityContextString = args.Message.ApplicationProperties.TryGetValue("SecurityContext", out var securityContextObj) ? securityContextObj.ToString() : "";
            var baggageString = args.Message.ApplicationProperties.TryGetValue("Baggage", out var baggageObj) ? baggageObj.ToString() : "";

            BlocksContext.SetContext(JsonSerializer.Deserialize<BlocksContext>(securityContextString));

            foreach (var kvp in DeserializeBaggage(baggageString))
            {
                Baggage.SetBaggage(kvp.Key, kvp.Value);
            }
            Baggage.SetBaggage("TenantId", tenantId);

            string messageId = args.Message.MessageId;
            var cancellationTokenSource = new CancellationTokenSource();
            var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
            _activeMessageRenewals.TryAdd(messageId, cancellationTokenSource);

            // Start a task to auto-renew the message lock
            MessageProcessingStarted?.Invoke(this, new AutoRenewalEventArgs
            {
                Args = args,
                Token = linkedTokenSource.Token,
                CancellationTokenSource = cancellationTokenSource
            });

            _logger.LogInformation("Received message: {MessageBody} at: {ReceivedAt}", args.Message.Body.ToString(), DateTimeOffset.Now);

            try
            {
                var parentActivityContext = new ActivityContext(
                    ActivityTraceId.CreateFromString(traceId),
                    spanId != null ? ActivitySpanId.CreateFromString(spanId.AsSpan()) : ActivitySpanId.CreateRandom(),
                    ActivityTraceFlags.Recorded,
                    traceState: null,
                    isRemote: true
                );

                using var activity = _activitySource.StartActivity("process.messaging.azure.service.bus", ActivityKind.Consumer, parentActivityContext);
                
                activity?.SetTag("message", args.Message);
                activity?.SetTag("SecurityContext", securityContextString);
                activity?.SetTag("messageId", messageId);

                string body = args.Message.Body.ToString();

                _logger.LogInformation("Message received: {Body}", body);

                activity?.SetTag("messaging.system", "azure.servicebus");
                activity?.SetTag("message.body", body);

                try
                {
                    // Start processing timer
                    var processingStopwatch = Stopwatch.StartNew();
                    _logger.LogInformation("Started processing message {MessageId} at: {StartTime}", messageId, DateTimeOffset.Now);

                    var message = JsonSerializer.Deserialize<Message>(body);
                    await _consumer.ProcessMessageAsync(message?.Type ?? string.Empty, message?.Body ?? string.Empty);

                    processingStopwatch.Stop();
                    _logger.LogInformation($"Completed processing message {messageId} in {processingStopwatch.ElapsedMilliseconds}ms");

                    activity?.SetTag("response", "Successfully Completed");
                    activity?.SetStatus(ActivityStatusCode.Ok, "Message processed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message {MessageId}: {ErrorMessage}", messageId, ex.Message);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.SetTag("error", ex.Message);
                }
                finally
                {
                    await cancellationTokenSource.CancelAsync();
                    linkedTokenSource.Dispose();
                    _activeMessageRenewals.TryRemove(messageId, out _);
                    await args.CompleteMessageAsync(args.Message);
                    activity?.Stop();
                    _logger.LogInformation($"Message processing time: {activity?.Duration} ms");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing message {MessageId}: {ErrorMessage}", messageId, ex.Message);
                await cancellationTokenSource.CancelAsync();
                linkedTokenSource.Dispose();
                _activeMessageRenewals.TryRemove(messageId, out _);
            }

            BlocksContext.ClearContext();
        }

        private static Dictionary<string, string> DeserializeBaggage(string? baggageString)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(baggageString ?? "{}")?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private async Task StartAutoRenewalTask(ProcessMessageEventArgs args, CancellationToken cancellationToken)
        {
            ServiceBusReceivedMessage message = args.Message;
            string messageId = message.MessageId;
            DateTime processingStartTime = DateTime.UtcNow;
            int renewalCount = 0;
            TimeSpan renewalInterval = TimeSpan.FromSeconds(_messageConfiguration?.AzureServiceBusConfiguration?.MessageLockRenewalIntervalSeconds ?? 270);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(renewalInterval, cancellationToken);

                    TimeSpan processingTime = DateTime.UtcNow - processingStartTime;
                    TimeSpan maxProcessingTime = TimeSpan.FromMinutes(_messageConfiguration?.AzureServiceBusConfiguration?.MaxMessageProcessingTimeInMinutes ?? 60);
                    if (processingTime > maxProcessingTime)
                    {
                        _logger.LogWarning("Message {MessageId} exceeded maximum processing time of {MaxProcessingTimeMinutes} minutes. " +
                                           "Stopping auto-renewal after {RenewalCount} renewals.", messageId, maxProcessingTime.TotalMinutes, renewalCount);
                        break;
                    }

                    try
                    {
                        await args.RenewMessageLockAsync(message, cancellationToken);
                        renewalCount++;
                        _logger.LogInformation("Renewed lock for message {MessageId} (renewal #{RenewalCount}, processing time: {ProcessingTimeSeconds:F1}s)",
                                                messageId, renewalCount, processingTime.TotalSeconds);
                    }
                    catch (Exception ex) when (ex is ServiceBusException or OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to renew lock for message {MessageId}: {ErrorMessage}", messageId, ex.Message);
                        break;
                    }
                }

                _logger.LogInformation("Auto-renewal for message {MessageId} completed after {RenewalCount} renewals", messageId, renewalCount);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogInformation(ex, "Auto-renewal for message {MessageId} was cancelled after {RenewalCount} renewals", messageId, renewalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-renewal task for message {MessageId}", messageId);
            }
        }

        private Task ErrorHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Service Bus error: {ExceptionMessage} (Entity: {EntityPath})", args.Exception.Message, args.EntityPath);
            return Task.CompletedTask;
        }
    }
}