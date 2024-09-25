using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            // Extract trace context from the message
            var traceId = args.Message.ApplicationProperties.TryGetValue("TraceId", out var traceIdObj) ? traceIdObj.ToString() : "";
            var spanId = args.Message.ApplicationProperties.TryGetValue("SpanId", out var spanIdObj) ? spanIdObj.ToString() : "";
            var traceState = args.Message.ApplicationProperties.TryGetValue("TraceState", out var traceStateObj) ? traceStateObj.ToString() : "";

            var securityContextString = args.Message.ApplicationProperties.TryGetValue("SecurityContext", out var securityContextObj) ? securityContextObj.ToString() : "";
            var securityContext = BlocksContext.GetContext(securityContextString);

            var activityContext = new ActivityContext(
                ActivityTraceId.CreateFromString(traceId),
                spanId != null ? ActivitySpanId.CreateFromString(spanId.AsSpan()) : ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded,
                traceState: null,  // Set the traceState from the incoming message
                isRemote: true
            );

            using var activity = _activitySource.StartActivity("ProcessMessage", ActivityKind.Consumer, activityContext);
            activity?.SetTag("message", args.Message);
            activity?.SetCustomProperty("SecurityContext", securityContextString);

            // TenantId is most important perameter, without this we cannot store the trace
            
            activity.SetCustomProperty("TenantId", securityContext?.TenantId);

            string body = args.Message.Body.ToString();
            _logger.LogInformation($"Message received: {body}");
            activity.SetCustomProperty("Request", body);

            try
            {
                var message = JsonConvert.DeserializeObject<Message>(body);

                await _consumer.ProcessMessageAsync(message.Type, message.Body);

                await args.CompleteMessageAsync(args.Message);

                activity.SetCustomProperty("Response", "Successfully Completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing message");
                activity.SetCustomProperty("Response", JsonConvert.SerializeObject(ex));
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
