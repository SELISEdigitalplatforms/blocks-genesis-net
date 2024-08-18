using Blocks.Genesis;
using WorkerTwo;


const string _serviceName = "Service-Worker-Test_Two";
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
                Queues = new List<string> { "demo_queue_1" },
                Topics = new List<string> { "demo_topic" },
                ServiceName = blocksSecrets.ServiceName,
            });

        });
