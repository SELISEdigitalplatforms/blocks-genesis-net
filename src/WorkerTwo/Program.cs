using Blocks.Genesis;
using WorkerTwo;


const string _serviceName = "Service-Worker-Test_Two";
var blocksSecrets = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);

var messageConfiguration = new MessageConfiguration
{
    Connection = blocksSecrets.MessageConnectionString,
    RabbitMqConfiguration = new()
    {
        ConsumerSubscriptions = new()
        {
            ConsumerSubscription.BindToQueue("test_from_cloud_queue_1", 2),
            ConsumerSubscription.BindToQueueViaExchange("test_from_cloud_queue_1", "test_from_cloud_exchange_1", 2),
        }
    },
    ServiceName = blocksSecrets.ServiceName,
};

await CreateHostBuilder(args).Build().RunAsync();

IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args).ConfigureServices((services) =>
        {
          //  services.AddSingleton<SecurityContext, BlocksContext>();
            services.AddHttpClient();

            services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
            services.AddSingleton<IConsumer<W2Context>, W2Consumer>();

            ApplicationConfigurations.ConfigureWorker(services, messageConfiguration);
        });
