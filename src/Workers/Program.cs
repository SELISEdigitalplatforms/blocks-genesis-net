using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using WorkerOne;

public class Program
{
    public static void Main(string[] args)
    {
        ApplicationConfigurations.ConfigureLog("Service-Worker-Test_One");
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices(async (hostContext, services) =>
            {

                services.AddHttpClient();

                ApplicationConfigurations.ConfigureServices(services, "Service-Worker-Test_One");

                services.AddHostedService<Worker>();

                // Add configuration for Azure Service Bus
                services.AddSingleton(serviceProvider =>
                {
                    string serviceBusConnectionString = "Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***";
                    return new ServiceBusClient(serviceBusConnectionString);
                });

            });
}
