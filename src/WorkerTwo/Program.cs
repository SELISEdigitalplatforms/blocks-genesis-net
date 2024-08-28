using Blocks.Genesis;
using Blocks.Genesis.Configuration;
using WorkerTwo;


const string _serviceName = "Service-Worker-Test_Two";
var blocksSecrets = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);

var messageConfiguration = new MessageConfiguration
{
    Connection = blocksSecrets.MessageConnectionString,
    Queues = new List<string> { "demo_queue" },
    Topics = new List<string> { "demo_topic", "demo_topic_1" },
    ServiceName = blocksSecrets.ServiceName,
};

await ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(messageConfiguration); 
await CreateHostBuilder(args).Build().RunAsync();

IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args).ConfigureServices((services) =>
        {
            services.AddSingleton<ISecurityContext, SecurityContext>();
            ApplicationConfigurations.ConfigureServices(services);
            services.AddHttpClient();

            services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
            services.AddSingleton<IConsumer<W2Context>, W2Consumer>();

            ApplicationConfigurations.ConfigureMessageConsumerAsync(services, messageConfiguration);
        });
