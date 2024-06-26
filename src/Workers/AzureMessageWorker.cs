using Azure.Messaging.ServiceBus;

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


        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogError("Cannot create azure service bus client");
            Task.CompletedTask.Wait();
        }


    }
}
