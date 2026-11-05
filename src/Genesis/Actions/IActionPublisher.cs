namespace Blocks.Genesis;

/// <summary>Publishes a Blocks action after a successful save. Registered by <c>ApplicationConfigurations.ConfigureServices</c>.</summary>
public interface IActionPublisher
{
    /// <summary>
    /// Best-effort: reads the current tenant's <see cref="ActionConstants.ActionKey"/> entry of
    /// <paramref name="action"/> and, when it has a target on, sends one <see cref="BlocksAction"/> per target.
    /// Never throws.
    /// </summary>
    /// <param name="action">The action name, matched ordinally.</param>
    /// <param name="data">Serialized to JSON; only the top-level fields named in the action's payload are kept.</param>
    /// <param name="organizationId"><c>null</c> takes the organization from <see cref="BlocksContext"/>.</param>
    /// <param name="cancellationToken">Cancels the entry read.</param>
    Task PublishAsync(string action, object? data, string? organizationId = null, CancellationToken cancellationToken = default);
}
