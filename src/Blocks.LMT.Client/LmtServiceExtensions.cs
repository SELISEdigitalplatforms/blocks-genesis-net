using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace SeliseBlocks.LMT.Client
{
    public static class LmtServiceExtensions
    {
        public static IServiceCollection AddLmtClient(this IServiceCollection services, Action<LmtOptions> configureOptions)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions));

            var options = new LmtOptions();
            configureOptions(options);
            
            // Validate options before registering
            options.Validate();

            services.AddSingleton(options);
            services.AddSingleton<ILmtMessageSender>(sp => LmtMessageSenderFactory.CreateShared(sp.GetRequiredService<LmtOptions>()));
            services.AddSingleton<IBlocksLogger, BlocksLogger>();

            return services;
        }

        public static IServiceCollection AddLmtClient(this IServiceCollection services, IConfiguration configuration)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var options = new LmtOptions();
            configuration.GetSection("Lmt").Bind(options);
            
            // Validate options before registering
            options.Validate();

            services.AddSingleton(options);
            services.AddSingleton<ILmtMessageSender>(sp => LmtMessageSenderFactory.CreateShared(sp.GetRequiredService<LmtOptions>()));
            services.AddSingleton<IBlocksLogger, BlocksLogger>();

            return services;
        }

        public static TracerProviderBuilder AddLmtTracing(this TracerProviderBuilder builder, LmtOptions options)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            return builder.AddProcessor(new LmtTraceProcessor(options));
        }
    }
}