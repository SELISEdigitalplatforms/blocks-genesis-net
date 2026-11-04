using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blocks.Genesis;

public static class ActionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IActionPublisher"/>. Needs <see cref="IKeyValueStore"/>, <see cref="IMessageClient"/> (optional) and
    /// an <see cref="System.Diagnostics.ActivitySource"/>, which <c>ConfigureServices</c> registers for API and worker hosts.
    /// </summary>
    public static IServiceCollection AddActions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        // The message client is optional: a host without a bus gets a publisher that logs and sends nothing.
        services.TryAddSingleton<IActionPublisher>(sp => new ActionPublisher(
            sp.GetRequiredService<IKeyValueStore>(),
            sp.GetService<IMessageClient>(),
            sp.GetRequiredService<ActivitySource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ActionPublisher>>()));
        return services;
    }
}
