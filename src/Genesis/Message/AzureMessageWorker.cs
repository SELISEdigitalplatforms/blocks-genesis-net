using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry.Trace;
using System.ComponentModel;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class AzureMessageWorker : BackgroundService
    {
        private readonly ILogger<AzureMessageWorker> _logger;
        private readonly List<ServiceBusProcessor> _processors = new List<ServiceBusProcessor>();
        private readonly MessageConfiguration _messageConfiguration;
        private readonly ActivitySource _activitySource;
        private ServiceBusClient _serviceBusClient;
        private Consumer _consumer;

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
                _logger.LogInformation("Service Bus Client initialized at: {time}", DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during initialization");
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
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
            _logger.LogInformation("Worker stopped at: {time}", DateTimeOffset.Now);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_serviceBusClient == null)
                {
                    _logger.LogError("Service Bus Client is not initialized");
                    return;
                }

                var queueProcessingTask = ProcessQueues(stoppingToken);
                var topicesProcessingTask = ProcessTopics(stoppingToken);

                await Task.WhenAll(queueProcessingTask, topicesProcessingTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ExecuteAsync");
                throw;
            }
        }

        private async Task ProcessQueues(CancellationToken stoppingToken)
        {
            foreach (var queueName in _messageConfiguration.Queues)
            {
                var processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
                {
                    PrefetchCount = _messageConfiguration.QueuePrefetchCount
                });
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(stoppingToken);
            }
        }

        private async Task ProcessTopics(CancellationToken stoppingToken)
        {
            foreach (var topicName in _messageConfiguration.Topics)
            {
                var processor = _serviceBusClient.CreateProcessor(topicName, _messageConfiguration.GetSubscriptionName(topicName), new ServiceBusProcessorOptions
                {
                    PrefetchCount = _messageConfiguration.TopicPrefetchCount
                });
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(stoppingToken);
            }
        }

        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            var traceId = args.Message.ApplicationProperties.TryGetValue("TraceId", out var traceIdObj) ? traceIdObj.ToString() : null;
            var spanId = args.Message.ApplicationProperties.TryGetValue("SpanId", out var spanIdObj) ? spanIdObj.ToString() : null;
            var parentSpanId = args.Message.ApplicationProperties.TryGetValue("ParentSpanId", out var parentSpanIdObj) ? parentSpanIdObj.ToString() : null;

            var activityContext = new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateFromString(spanId.AsSpan()),
                ActivityTraceFlags.Recorded,
                traceState: traceId,
                isRemote: true
            );

            using var activity = _activitySource.StartActivity("ProcessMessage", ActivityKind.Consumer, activityContext);
            activity?.SetTag("message.id", args.Message.MessageId);

            // Start timer
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            string body = args.Message.Body.ToString();
            _logger.LogInformation($"Message received: {body}");

            try
            {
                var message = JsonConvert.DeserializeObject<Message>(body);

                await _consumer.ProcessMessageAsync(message.Type, message.Body);

                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing message");
            }
            finally
            {
                // Stop timer and log execution time
                stopwatch.Stop();
                _logger.LogInformation($"Message processing time: {stopwatch.ElapsedMilliseconds} ms");
                activity?.Stop();
            }
        }

        private Task ErrorHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Error processing message");
            return Task.CompletedTask;
        }
    }
}
