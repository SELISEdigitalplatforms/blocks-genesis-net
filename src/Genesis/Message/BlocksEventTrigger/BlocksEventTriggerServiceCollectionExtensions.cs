using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blocks.Genesis;

internal static class BlocksEventTriggerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the catalog cache and the forwarder as singletons. The message clients' longer constructor takes
    /// <see cref="IBlocksEventTriggerForwarder"/>, so registering it turns forwarding on.
    /// </summary>
    internal static IServiceCollection AddBlocksEventTrigger(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBlocksEventCatalogCache, BlocksEventCatalogCache>();
        services.TryAddSingleton<IBlocksEventTriggerForwarder, BlocksEventTriggerForwarder>();
        return services;
    }
}
