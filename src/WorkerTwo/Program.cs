using Blocks.Genesis;
using MassTransit;
using System.Reflection;
using WorkerTwo;

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
                services.AddMassTransit(x =>
                {
                    var entryAssembly = Assembly.GetExecutingAssembly();
                    x.AddConsumers(entryAssembly);
                    x.UsingRabbitMq((context, cfg) =>
                    {
                        cfg.Host(new Uri("rabbitmq://10.30.65.4:5672/"), h =>
                        {
                            h.Username("test");
                            h.Password("test");
                        });
                        cfg.ConfigureEndpoints(context);
                    });

                });

                services.AddHttpClient();
                services.AddHostedService<Worker>();

                ApplicationConfigurations.ConfigureServices(services, "Service-Worker-Test_Two");


            });
}
