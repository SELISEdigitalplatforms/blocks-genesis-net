using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Blocks.Genesis
{
    internal class AzureMessageWorker : BackgroundService
    {
        private readonly ILogger<AzureMessageWorker> _logger;
        private readonly List<ServiceBusProcessor> _processors = new List<ServiceBusProcessor>();
        private readonly MessageConfiguration _messageConfiguration;
        private ServiceBusClient _serviceBusClient;
        private Consumer _consumer;

        public AzureMessageWorker(ILogger<AzureMessageWorker> logger, MessageConfiguration messageConfiguration, Consumer consumer)
        {
            _logger = logger;
            _messageConfiguration = messageConfiguration;
            _consumer = consumer;

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

                await ProcessQueues(stoppingToken);
                await ProcessTopics(stoppingToken);
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
        }

        private Task ErrorHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Error processing message");
            return Task.CompletedTask;
        }


    }
}
