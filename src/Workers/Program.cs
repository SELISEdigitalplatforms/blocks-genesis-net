using Blocks.Genesis;
using WorkerOne;


const string _serviceName = "Service-Worker-Test_One";
var blocksSecrets = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);

var messageConfiguration = new MessageConfiguration
{
    Queues = new List<string> { "demo_queue" },
    Topics = new List<string> { "demo_topic", "demo_topic_1" }
};

await ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(messageConfiguration);
await CreateHostBuilder(args).Build().RunAsync();

IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args).ConfigureServices((services) =>
        {

            //  services.AddSingleton<SecurityContext, BlocksContext>();
            ApplicationConfigurations.ConfigureServices(services, messageConfiguration);
            services.AddHttpClient();

            services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
            services.AddSingleton<IConsumer<W2Context>, W2Consumer>();
        });
