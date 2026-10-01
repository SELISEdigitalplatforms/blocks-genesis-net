namespace Blocks.Genesis;

/// <summary>
/// Meter ids as the catalogue spells them. Genesis only needs the handful it touches itself;
/// every other meter is named by the service that owns it, because the service is the source of
/// truth for what its own units mean.
/// </summary>
public static class BlocksQuotaMeters
{
    /// <summary>Counted automatically for every request that resolves a non-root tenant.</summary>
    public const string ApiCalls = "api.calls";
}
