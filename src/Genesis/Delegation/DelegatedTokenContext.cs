namespace Blocks.Genesis;

/// <summary>
/// Ambient holder for the delegation grant id the current unit of work may redeem.
/// <para>
/// The worker sets this from the <c>DelegationGrant</c> message header before dispatching to the
/// handler, and clears it after the message settles. The value is a bearer credential: it must
/// never be logged, set as an <c>Activity</c> tag, or placed in Baggage.
/// </para>
/// </summary>
public static class DelegatedTokenContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The grant id for the current logical flow, or <c>null</c> when there is none.</summary>
    public static string? Current => _current.Value;

    /// <summary>True when a grant id is present.</summary>
    public static bool HasGrant => !string.IsNullOrWhiteSpace(_current.Value);

    /// <summary>
    /// Sets the grant id. A malformed value is treated as absent so the flow fails closed
    /// rather than sending garbage to the exchange endpoint.
    /// </summary>
    public static void Set(string? delegationGrantId)
    {
        _current.Value = DelegationGrantStore.IsWellFormed(delegationGrantId) ? delegationGrantId : null;
    }

    public static void Clear() => _current.Value = null;
}
