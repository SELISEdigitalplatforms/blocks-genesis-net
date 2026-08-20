using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Blocks.Genesis;

/// <summary>
/// Builds the grant that a send attaches to a message. Shared by the Azure and RabbitMQ clients so
/// both produce byte-identical headers.
/// </summary>
public interface IDelegationGrantFactory
{
    /// <summary>
    /// Creates a grant for the message about to be sent, or returns <c>null</c> when the current
    /// flow has no authenticated user to delegate. One grant per logical message; never reused.
    /// </summary>
    Task<string?> CreateForSendAsync(TimeSpan? ttl = null);
}

/// <summary>
/// <para>
/// <c>token_version</c> and <c>security_stamp</c> are read straight off <c>HttpContext.User</c>
/// claims — they are not on <see cref="BlocksContext"/> — so a send costs no extra I/O.
/// </para>
/// <para>
/// A worker-originated send (chained delegation) has no <c>HttpContext</c>. There the two values
/// are carried forward from the grant the worker is already holding.
/// </para>
/// <para>
/// With no authenticated user in context there is no grant and the header is omitted: the flow
/// fails closed rather than minting a token nobody asked for.
/// </para>
/// </summary>
public sealed class DelegationGrantFactory : IDelegationGrantFactory
{
    public const string TokenVersionClaim = "token_version";
    public const string SecurityStampClaim = "security_stamp";

    private readonly IDelegationGrantStore _grantStore;
    private readonly ILogger<DelegationGrantFactory> _logger;

    public DelegationGrantFactory(IDelegationGrantStore grantStore, ILogger<DelegationGrantFactory> logger)
    {
        _grantStore = grantStore;
        _logger = logger;
    }

    public async Task<string?> CreateForSendAsync(TimeSpan? ttl = null)
    {
        var context = BlocksContext.GetContext();

        if (context is null
            || !context.IsAuthenticated
            || string.IsNullOrWhiteSpace(context.TenantId)
            || string.IsNullOrWhiteSpace(context.UserId))
        {
            return null;
        }

        var (tokenVersion, securityStamp) = ReadFromHttpUser();

        if (string.IsNullOrWhiteSpace(tokenVersion) && string.IsNullOrWhiteSpace(securityStamp))
        {
            (tokenVersion, securityStamp) = await ReadFromHeldGrantAsync(context.TenantId).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(tokenVersion) || string.IsNullOrWhiteSpace(securityStamp))
        {
            // A grant without these cannot be redeemed: IAM compares both against the tenant DB.
            DelegationGrantFactoryLog.NoVersionMaterial(_logger);
            return null;
        }

        try
        {
            return await _grantStore.CreateAsync(context, tokenVersion!, securityStamp!, ttl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A send must not fail because delegation could not be set up. The message still goes
            // out, just without user context downstream.
            DelegationGrantFactoryLog.CreateFailed(_logger, ex);
            return null;
        }
    }

    private static (string? TokenVersion, string? SecurityStamp) ReadFromHttpUser()
    {
        try
        {
            if (BlocksHttpContextAccessor.Instance?.HttpContext?.User?.Identity is not ClaimsIdentity identity
                || !identity.IsAuthenticated)
            {
                return (null, null);
            }

            return (identity.FindFirst(TokenVersionClaim)?.Value, identity.FindFirst(SecurityStampClaim)?.Value);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<(string? TokenVersion, string? SecurityStamp)> ReadFromHeldGrantAsync(string tenantId)
    {
        var heldGrantId = DelegatedTokenContext.Current;
        if (string.IsNullOrWhiteSpace(heldGrantId)) return (null, null);

        var record = await _grantStore.GetAsync(heldGrantId!).ConfigureAwait(false);
        if (record is null) return (null, null);

        if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal))
        {
            DelegationGrantFactoryLog.HeldGrantTenantMismatch(_logger);
            return (null, null);
        }

        return (record.TokenVersion, record.SecurityStamp);
    }
}

internal static partial class DelegationGrantFactoryLog
{
    [LoggerMessage(EventId = 7030, Level = LogLevel.Debug, Message = "No token_version/security_stamp available for the current flow; sending without a delegation grant.")]
    public static partial void NoVersionMaterial(ILogger logger);

    [LoggerMessage(EventId = 7031, Level = LogLevel.Warning, Message = "The held delegation grant belongs to a different tenant than the current context; not chaining it.")]
    public static partial void HeldGrantTenantMismatch(ILogger logger);

    [LoggerMessage(EventId = 7032, Level = LogLevel.Error, Message = "Could not create a delegation grant; the message is sent without one.")]
    public static partial void CreateFailed(ILogger logger, Exception exception);
}
