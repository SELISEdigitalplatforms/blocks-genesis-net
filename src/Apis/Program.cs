using Blocks.Genesis;

namespace Api1
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ApplicationConfigurations.ConfigureLog("Service-API-Test_Two");
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }

}
