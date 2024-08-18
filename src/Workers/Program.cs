using Blocks.Genesis;
using WorkerOne;


const string _serviceName = "Service-Worker-Test_One";
var blocksSecrets = await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName);
CreateHostBuilder(args).Build().Run();

IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args).ConfigureServices(async (hostContext, services) =>
        {

            ApplicationConfigurations.ConfigureServices(services);

            services.AddHttpClient();


            services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
            services.AddSingleton<IConsumer<W2Context>, W2Consumer>();

            await ApplicationConfigurations.ConfigureMessageWorker(services, new MessageConfiguration
            {
                Connection = blocksSecrets.MessageConnectionString,
                Queues = new List<string> { "demo_queue" },
                Topics = new List<string> { "demo_topic", "demo_topic_1" },
                ServiceName = blocksSecrets.ServiceName,
            });
        });
