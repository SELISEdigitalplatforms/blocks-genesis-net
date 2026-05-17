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
                    AzureMessageWorkerLog.ConnectionStringMissing(_logger);
                    return;
                }

                _serviceBusClient = new ServiceBusClient(_messageConfiguration.Connection);
                AzureMessageWorkerLog.ClientInitialized(_logger, DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                AzureMessageWorkerLog.InitializationFailed(_logger, ex);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Cancel all active message lock renewals
            foreach (var renewalPair in _activeMessageRenewals)
            {
               await renewalPair.Value.CancelAsync().ConfigureAwait(false);
            }
            _activeMessageRenewals.Clear();

            foreach (var processor in _processors)
            {
                try
                {
                    await processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AzureMessageWorkerLog.StopProcessorFailed(_logger, ex);
                }
                finally
                {
                    await processor.DisposeAsync().ConfigureAwait(false);
                }
            }

            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            AzureMessageWorkerLog.WorkerStopped(_logger, DateTimeOffset.Now);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_serviceBusClient == null)
                {
                    AzureMessageWorkerLog.ClientNotInitialized(_logger);
                    throw new InvalidOperationException("Service Bus Client is not initialized");
                }

                MessageProcessingStarted += async (sender, e) =>
                {
                   await StartAutoRenewalTask(e.Args, e.Token).ConfigureAwait(false);
                };

                var queueProcessingTask = ProcessQueues(stoppingToken);
                var topicesProcessingTask = ProcessTopics(stoppingToken);

                await Task.WhenAll(queueProcessingTask, topicesProcessingTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AzureMessageWorkerLog.ExecuteAsyncFailed(_logger, ex);
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
                await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
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
                await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
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

            try
            {
                BlocksContext.SetContext(JsonSerializer.Deserialize<BlocksContext>(securityContextString));
            }
            catch (JsonException)
            {
                BlocksContext.SetContext(null);
            }

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

            AzureMessageWorkerLog.MessageReceived(_logger, messageId, DateTimeOffset.Now);

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
                

                activity?.SetTag("SecurityContext", securityContextString);
                activity?.SetTag("messageId", messageId);

                string body = args.Message.Body.ToString();

                activity?.SetTag("messaging.system", "azure.servicebus");
                activity?.SetTag("usage", true);

                var isProcessedSuccessfully = false;
                Exception? processingException = null;

                try
                {
                    // Start processing timer
                    var processingStopwatch = Stopwatch.StartNew();
                    AzureMessageWorkerLog.MessageProcessingStarted(_logger, messageId, DateTimeOffset.Now);

                    var message = JsonSerializer.Deserialize<Message>(body);
                    await _consumer.ProcessMessageAsync(message?.Type ?? string.Empty, message?.Body ?? string.Empty).ConfigureAwait(false);

                    processingStopwatch.Stop();
                    AzureMessageWorkerLog.MessageProcessingCompleted(_logger, messageId, processingStopwatch.ElapsedMilliseconds);

                    activity?.SetTag("response", "Successfully Completed");
                    activity?.SetStatus(ActivityStatusCode.Ok, "Message processed successfully");
                    isProcessedSuccessfully = true;
                }
                catch (Exception ex)
                {
                    AzureMessageWorkerLog.MessageProcessingFailed(_logger, messageId, ex.Message, ex);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.SetTag("error", ex.Message);
                    processingException = ex;
                }
                finally
                {
                    await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    linkedTokenSource.Dispose();
                    _activeMessageRenewals.TryRemove(messageId, out _);

                    if (isProcessedSuccessfully)
                    {
                        await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            await args.DeadLetterMessageAsync(
                                args.Message,
                                deadLetterReason: "processing_failed",
                                deadLetterErrorDescription: processingException?.GetType().Name ?? "unknown_error").ConfigureAwait(false);
                        }
                        catch (Exception deadLetterException)
                        {
                            AzureMessageWorkerLog.DeadLetterFailed(_logger, messageId, deadLetterException);
                        }
                    }

                    activity?.Stop();
                    AzureMessageWorkerLog.MessageProcessingDuration(_logger, activity?.Duration ?? TimeSpan.Zero);
                }
            }
            catch (Exception ex)
            {
                AzureMessageWorkerLog.UnexpectedProcessingError(_logger, messageId, ex.Message, ex);
                await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
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
                    await Task.Delay(renewalInterval, cancellationToken).ConfigureAwait(false);

                    TimeSpan processingTime = DateTime.UtcNow - processingStartTime;
                    TimeSpan maxProcessingTime = TimeSpan.FromMinutes(_messageConfiguration?.AzureServiceBusConfiguration?.MaxMessageProcessingTimeInMinutes ?? 60);
                    if (processingTime > maxProcessingTime)
                    {
                        AzureMessageWorkerLog.MaxProcessingExceeded(_logger, messageId, maxProcessingTime.TotalMinutes, renewalCount);
                        break;
                    }

                    try
                    {
                        await args.RenewMessageLockAsync(message, cancellationToken).ConfigureAwait(false);
                        renewalCount++;
                        AzureMessageWorkerLog.MessageLockRenewed(_logger, messageId, renewalCount, processingTime.TotalSeconds);
                    }
                    catch (Exception ex) when (ex is ServiceBusException or OperationCanceledException)
                    {
                        AzureMessageWorkerLog.RenewLockFailed(_logger, messageId, ex.Message, ex);
                        break;
                    }
                }

                AzureMessageWorkerLog.AutoRenewCompleted(_logger, messageId, renewalCount);
            }
            catch (OperationCanceledException ex)
            {
                AzureMessageWorkerLog.AutoRenewCancelled(_logger, messageId, renewalCount, ex);
            }
            catch (Exception ex)
            {
                AzureMessageWorkerLog.AutoRenewError(_logger, messageId, ex);
            }
        }

        private Task ErrorHandler(ProcessErrorEventArgs args)
        {
            AzureMessageWorkerLog.ServiceBusError(_logger, args.Exception.Message, args.EntityPath, args.Exception);
            return Task.CompletedTask;
        }
    }

    internal static partial class AzureMessageWorkerLog
    {
        [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "Connection string missing")]
        public static partial void ConnectionStringMissing(ILogger logger);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Service Bus Client initialized at: {Time}")]
        public static partial void ClientInitialized(ILogger logger, DateTimeOffset time);

        [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Error during initialization")]
        public static partial void InitializationFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "Error stopping processor")]
        public static partial void StopProcessorFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Worker stopped at: {Time}")]
        public static partial void WorkerStopped(ILogger logger, DateTimeOffset time);

        [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "Service Bus Client is not initialized")]
        public static partial void ClientNotInitialized(ILogger logger);

        [LoggerMessage(EventId = 3007, Level = LogLevel.Error, Message = "Error in ExecuteAsync")]
        public static partial void ExecuteAsyncFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3008, Level = LogLevel.Information, Message = "Received message {MessageId} at: {ReceivedAt}")]
        public static partial void MessageReceived(ILogger logger, string messageId, DateTimeOffset receivedAt);

        [LoggerMessage(EventId = 3009, Level = LogLevel.Information, Message = "Started processing message {MessageId} at: {StartTime}")]
        public static partial void MessageProcessingStarted(ILogger logger, string messageId, DateTimeOffset startTime);

        [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "Completed processing message {MessageId} in {ProcessingTime}ms")]
        public static partial void MessageProcessingCompleted(ILogger logger, string messageId, long processingTime);

        [LoggerMessage(EventId = 3011, Level = LogLevel.Error, Message = "Error processing message {MessageId}: {ErrorMessage}")]
        public static partial void MessageProcessingFailed(ILogger logger, string messageId, string errorMessage, Exception exception);

        [LoggerMessage(EventId = 3012, Level = LogLevel.Error, Message = "Failed to dead-letter message {MessageId}.")]
        public static partial void DeadLetterFailed(ILogger logger, string messageId, Exception exception);

        [LoggerMessage(EventId = 3013, Level = LogLevel.Information, Message = "Message processing time: {Duration} ms")]
        public static partial void MessageProcessingDuration(ILogger logger, TimeSpan duration);

        [LoggerMessage(EventId = 3014, Level = LogLevel.Error, Message = "Unexpected error processing message {MessageId}: {ErrorMessage}")]
        public static partial void UnexpectedProcessingError(ILogger logger, string messageId, string errorMessage, Exception exception);

        [LoggerMessage(EventId = 3015, Level = LogLevel.Warning, Message = "Message {MessageId} exceeded maximum processing time of {MaxProcessingTimeMinutes} minutes. Stopping auto-renewal after {RenewalCount} renewals.")]
        public static partial void MaxProcessingExceeded(ILogger logger, string messageId, double maxProcessingTimeMinutes, int renewalCount);

        [LoggerMessage(EventId = 3016, Level = LogLevel.Information, Message = "Renewed lock for message {MessageId} (renewal #{RenewalCount}, processing time: {ProcessingTimeSeconds}s)")]
        public static partial void MessageLockRenewed(ILogger logger, string messageId, int renewalCount, double processingTimeSeconds);

        [LoggerMessage(EventId = 3017, Level = LogLevel.Warning, Message = "Failed to renew lock for message {MessageId}: {ErrorMessage}")]
        public static partial void RenewLockFailed(ILogger logger, string messageId, string errorMessage, Exception exception);

        [LoggerMessage(EventId = 3018, Level = LogLevel.Information, Message = "Auto-renewal for message {MessageId} completed after {RenewalCount} renewals")]
        public static partial void AutoRenewCompleted(ILogger logger, string messageId, int renewalCount);

        [LoggerMessage(EventId = 3019, Level = LogLevel.Information, Message = "Auto-renewal for message {MessageId} was cancelled after {RenewalCount} renewals")]
        public static partial void AutoRenewCancelled(ILogger logger, string messageId, int renewalCount, Exception exception);

        [LoggerMessage(EventId = 3020, Level = LogLevel.Error, Message = "Error in auto-renewal task for message {MessageId}")]
        public static partial void AutoRenewError(ILogger logger, string messageId, Exception exception);

        [LoggerMessage(EventId = 3021, Level = LogLevel.Error, Message = "Service Bus error: {ExceptionMessage} (Entity: {EntityPath})")]
        public static partial void ServiceBusError(ILogger logger, string exceptionMessage, string entityPath, Exception exception);
    }
}
