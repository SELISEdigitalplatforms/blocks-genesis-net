using Blocks.Genesis;

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

                await MessageReceiver.ReceiveMessagesAsync();

            });
}
