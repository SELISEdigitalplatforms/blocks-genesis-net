using Blocks.Genesis;
using WorkerTwo;

public class Program
{
    public static void Main(string[] args)
    {
        ApplicationConfigurations.ConfigureLog("Service-Worker-Test_Two");
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {

                services.AddHttpClient();

                ApplicationConfigurations.ConfigureServices(services, "Service-Worker-Test_Two");


                services.AddSingleton<IConsumer<W1Context>, W1Consumer>();
                services.AddSingleton<IConsumer<W2Context>, W2Consumer>();

                ApplicationConfigurations.ConfigureMessageWorker(services, new MessageConfiguration
                {
                    Connection = "Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***",
                    Queues = new List<string> { "demo_queue_1" },
                    Topics = new List<string> { "demo_topic_1" }
                });




            });
}
