using Blocks.Genesis;

public class Program
{
    public static async Task Main(string[] args)
    {
        ApplicationConfigurations.ConfigureLog("Service-Worker-Test_One");
        await CreateHostBuilder(args).Build().RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {


                services.AddHttpClient();

                ApplicationConfigurations.ConfigureServices(services, "Service-Worker-Test_Two");


            });
}
