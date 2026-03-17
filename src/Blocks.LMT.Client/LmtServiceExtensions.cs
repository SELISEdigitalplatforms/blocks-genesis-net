using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace SeliseBlocks.LMT.Client
{
    public static class LmtServiceExtensions
    {
        public static IServiceCollection AddLmtClient(this IServiceCollection services, Action<LmtOptions> configureOptions)
        {
            var options = new LmtOptions();
            configureOptions(options);

            services.AddSingleton(options);
            services.AddSingleton<IBlocksLogger, BlocksLogger>();

            return services;
        }

        public static IServiceCollection AddLmtClient(this IServiceCollection services, IConfiguration configuration)
        {
            var options = new LmtOptions();
            configuration.GetSection("Lmt").Bind(options);

            services.AddSingleton(options);
            services.AddSingleton<IBlocksLogger, BlocksLogger>();

            return services;
        }

        public static TracerProviderBuilder AddLmtTracing(this TracerProviderBuilder builder, LmtOptions options)
        {
            return builder.AddProcessor(new LmtTraceProcessor(options));
        }
    }
}