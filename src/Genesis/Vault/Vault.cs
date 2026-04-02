using Microsoft.Extensions.DependencyInjection;

namespace Blocks.Genesis
{
    public static class SecretServiceExtensions
    {
        public static IServiceCollection AddGenesisSecrets(
            this IServiceCollection services,
            Action<GenesisSecretOptions> configure)
        {
            var options = new GenesisSecretOptions();
            configure(options);
            services.AddSingleton(options);

            switch (options.Mode)
            {
                case SecretMode.Azure:
                    services.AddSingleton<ISecretProvider, AzureSecretProvider>();
                    break;

                case SecretMode.OnPrem:
                    services.AddSingleton<ISecretProvider, LocalSecretProvider>();
                    break;

                case SecretMode.Platform:
                    services.AddHttpClient<ISecretProvider, PlatformSecretProvider>();
                    break;

                default:
                    throw new InvalidOperationException($"Unknown SecretMode: {options.Mode}");
            }

            return services;
        }
    }
}
