using Workers;

namespace Workers
{
    internal class Program
    {
        protected Program() { }

        private static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddHostedService<Worker>();

            var host = builder.Build();
            host.Run();
        }
    }
}