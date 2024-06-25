using Azure.Messaging.ServiceBus.Administration;

namespace Blocks.Genesis
{
    internal class ConfigAzureServiceBus
    {
        private readonly ServiceBusAdministrationClient _adminClient;
        private const string ConnectionString = "Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***";

        public ConfigAzureServiceBus()
        {
            _adminClient = new ServiceBusAdministrationClient(ConnectionString);
        }

        internal async Task CreateQueuesAsync()
        {
            try
            {
                var isExist = await CheckQueueExistsAsync("DemoQueue");
                if (isExist) return;

                var createQueueOptions = new CreateQueueOptions("DemoQueue");
                createQueueOptions.MaxSizeInMegabytes = 2048;
                createQueueOptions.MaxDeliveryCount = 5;
                createQueueOptions.DefaultMessageTimeToLive = TimeSpan.FromSeconds(3600 * 24 * 7);

                await _adminClient.CreateQueueAsync(createQueueOptions);
            }
            catch (Exception e)
            {

                Console.WriteLine(e);
            }
        }

        async Task<bool> CheckQueueExistsAsync(string queue)
        {
            return await _adminClient.QueueExistsAsync(queue);
        }
    }
}
