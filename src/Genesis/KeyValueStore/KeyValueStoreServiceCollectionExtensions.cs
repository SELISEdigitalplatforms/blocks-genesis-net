using Microsoft.Extensions.DependencyInjection;

namespace Blocks.Genesis;

public static class KeyValueStoreServiceCollectionExtensions
{
    public static IServiceCollection AddKeyValueStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IKeyValueStore, MongoKeyValueStore>();
        return services;
    }
}
