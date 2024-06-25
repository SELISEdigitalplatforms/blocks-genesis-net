using Azure.Messaging.ServiceBus;

namespace WorkerOne
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly List<string> _queueNames = new List<string> { "DemoQueue", "DemoQueue1" };
        private readonly List<ServiceBusProcessor> _processors = new List<ServiceBusProcessor>();

        private readonly Dictionary<string, (string topicName, string subscriptionName)> _topicSubscriptions = new Dictionary<string, (string, string)>
        {
            {"Subscription1", ("<your_topic_name_1>", "<your_subscription_name_1>")},
            {"Subscription2", ("<your_topic_name_2>", "<your_subscription_name_2>")}
        };

        public Worker(ILogger<Worker> logger, ServiceBusClient serviceBusClient)
        {
            _logger = logger;
            _serviceBusClient = serviceBusClient;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // Register processors for queues
            foreach (var queueName in _queueNames)
            {
                var processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions());
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(cancellationToken);
            }

            // Register processors for topic subscriptions
            foreach (var (topicName, subscriptionName) in _topicSubscriptions.Values)
            {
                var processor = _serviceBusClient.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions());
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(cancellationToken);
            }

            _logger.LogInformation("Worker started at: {time}", DateTimeOffset.Now);
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

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // No need to implement this method since the work is done in StartAsync and StopAsync
            return Task.CompletedTask;
        }

        private async Task MessageHandler(ProcessMessageEventArgs args)
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

        private Task ErrorHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Error processing message");
            return Task.CompletedTask;
        }
    }
}
