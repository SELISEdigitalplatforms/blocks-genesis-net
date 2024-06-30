using Blocks.Genesis;
using WorkerOne;


const string _serviceName = "Service-Worker-Test_One";
ApplicationConfigurations.SetServiceName(_serviceName);
ApplicationConfigurations.ConfigureLog();
CreateHostBuilder(args).Build().Run();

IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
        {
            services.AddHttpClient();

            ApplicationConfigurations.ConfigureServices(services);


            services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
            services.AddSingleton<IConsumer<W2Context>, W2Consumer>();

            ApplicationConfigurations.ConfigureMessageWorker(services, new MessageConfiguration
            {
                Connection = "Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***",
                Queues = new List<string> { "demo_queue" },
                Topics = new List<string> { "demo_topic", "demo_topic_1" },
                ServiceName = _serviceName
            });
        });
