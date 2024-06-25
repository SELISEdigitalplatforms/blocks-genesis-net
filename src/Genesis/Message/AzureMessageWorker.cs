using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis
{
    internal class AzureMessageWorker : BackgroundService
    {
        private readonly ILogger<AzureMessageWorker> _logger;
        private readonly List<ServiceBusProcessor> _processors = new List<ServiceBusProcessor>(); 
        private readonly MessageConfiguration _messageConfiguration;

        private ServiceBusClient? _serviceBusClient;

        public AzureMessageWorker(ILogger<AzureMessageWorker> logger, MessageConfiguration messageConfiguration)
        {
            _logger = logger;
            _messageConfiguration = messageConfiguration;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_messageConfiguration.Connection))
                {
                    _logger.LogError("Connection string missing");
                    return;
                }

                _serviceBusClient = new ServiceBusClient(_messageConfiguration.Connection);

                _logger.LogInformation("Worker started at: {time}", DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping StartAsync");
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

        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_serviceBusClient == null)
                {
                    _logger.LogError("Cannot create azure service bus client");
                    return;
                }

                // Register processors for queues
                await ProcessQueues(stoppingToken);

                // Register processors for topic subscriptions
                await ProcessTopics(stoppingToken);


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping ExecuteAsync");
                throw;
            }
        }

        async Task ProcessQueues(CancellationToken stoppingToken)
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

        async Task ProcessTopics(CancellationToken stoppingToken)
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

        async Task MessageHandler(ProcessMessageEventArgs args)
        {
            string body = args.Message.Body.ToString();
            _logger.LogInformation($"Message received: {body}");

            try
            {
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing message");
            }
        }

        Task ErrorHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Error processing message");
            return Task.CompletedTask;
        }
    }
}
