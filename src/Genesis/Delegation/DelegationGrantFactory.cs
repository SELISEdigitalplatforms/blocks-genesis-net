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
    /// flow has no authenticated user or client to delegate. One grant per logical message; never reused.
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
/// A <c>client_credentials</c> caller (a token with <c>client_id</c> and no <c>user_id</c>) gets a
/// client grant instead, which needs no version material.
/// </para>
/// <para>
/// With neither an authenticated user nor a client in context there is no grant and the header is
/// omitted: the flow fails closed rather than minting a token nobody asked for.
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
            || string.IsNullOrWhiteSpace(context.TenantId))
        {
            return null;
        }

        // A user token may also carry client_id (the OIDC client it was issued to); the user wins.
        if (!string.IsNullOrWhiteSpace(context.UserId))
        {
            return await CreateForUserAsync(context, ttl).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(context.ClientId))
        {
            return await CreateForClientAsync(context, ttl).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> CreateForUserAsync(BlocksContext context, TimeSpan? ttl)
    {
        var (tokenVersion, securityStamp) = ReadFromHttpUser();

        if (string.IsNullOrWhiteSpace(tokenVersion) && string.IsNullOrWhiteSpace(securityStamp))
        {
            (tokenVersion, securityStamp) = await ReadFromHeldGrantAsync(context).ConfigureAwait(false);
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

    /// <summary>
    /// A <c>client_credentials</c> caller. In an API request the client id comes from the validated
    /// token. In a worker it comes from the held grant, never from the message
    /// <c>SecurityContext</c> alone: a client grant is only chained from another client grant.
    /// </summary>
    private async Task<string?> CreateForClientAsync(BlocksContext context, TimeSpan? ttl)
    {
        string? clientId;
        string? organizationId;

        var httpClientId = ReadClientIdFromHttpUser();
        if (!string.IsNullOrWhiteSpace(httpClientId))
        {
            clientId = httpClientId;
            organizationId = context.OrganizationId;
        }
        else
        {
            var held = await ReadHeldGrantAsync(context.TenantId).ConfigureAwait(false);
            if (held is null || !held.IsClientGrant)
            {
                DelegationGrantFactoryLog.NoClientGrantToChain(_logger);
                return null;
            }

            if (!string.Equals(held.ClientId, context.ClientId, StringComparison.Ordinal))
            {
                DelegationGrantFactoryLog.HeldGrantClientMismatch(_logger);
                return null;
            }

            clientId = held.ClientId;
            organizationId = held.OrganizationId;
        }

        try
        {
            return await _grantStore.CreateForClientAsync(context.TenantId, clientId!, organizationId, ttl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DelegationGrantFactoryLog.CreateFailed(_logger, ex);
            return null;
        }
    }

    /// <summary>The <c>client_id</c> of a validated client token (one with no <c>user_id</c>), or null.</summary>
    private static string? ReadClientIdFromHttpUser()
    {
        try
        {
            if (BlocksHttpContextAccessor.Instance?.HttpContext?.User?.Identity is not ClaimsIdentity identity
                || !identity.IsAuthenticated
                || !string.IsNullOrWhiteSpace(identity.FindFirst(BlocksContext.USER_ID_CLAIM)?.Value))
            {
                return null;
            }

            return identity.FindFirst(BlocksContext.CLIENT_ID_CLAIM)?.Value;
        }
        catch
        {
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

    /// <summary>
    /// In a worker, the user and organization on <see cref="BlocksContext"/> came from the message
    /// <c>SecurityContext</c>. The new grant is written from that context, so it must name the same
    /// user and organization as the held grant, or a tampered message could redirect the chain.
    /// </summary>
    private async Task<(string? TokenVersion, string? SecurityStamp)> ReadFromHeldGrantAsync(BlocksContext context)
    {
        var record = await ReadHeldGrantAsync(context.TenantId).ConfigureAwait(false);
        if (record is null || record.IsClientGrant) return (null, null);

        if (!string.Equals(record.UserId, context.UserId, StringComparison.Ordinal)
            || !string.Equals(record.OrganizationId ?? string.Empty, context.OrganizationId ?? string.Empty, StringComparison.Ordinal))
        {
            DelegationGrantFactoryLog.HeldGrantUserMismatch(_logger);
            return (null, null);
        }

        return (record.TokenVersion, record.SecurityStamp);
    }

    private async Task<DelegationGrantRecord?> ReadHeldGrantAsync(string tenantId)
    {
        var heldGrantId = DelegatedTokenContext.Current;
        if (string.IsNullOrWhiteSpace(heldGrantId)) return null;

        var record = await _grantStore.GetAsync(heldGrantId!).ConfigureAwait(false);
        if (record is null) return null;

        if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal))
        {
            DelegationGrantFactoryLog.HeldGrantTenantMismatch(_logger);
            return null;
        }

        return record;
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

    [LoggerMessage(EventId = 7033, Level = LogLevel.Debug, Message = "Client context with no validated client token and no held client grant; sending without a delegation grant.")]
    public static partial void NoClientGrantToChain(ILogger logger);

    [LoggerMessage(EventId = 7034, Level = LogLevel.Warning, Message = "The held client delegation grant names a different client than the current context; not chaining it.")]
    public static partial void HeldGrantClientMismatch(ILogger logger);

    [LoggerMessage(EventId = 7035, Level = LogLevel.Warning, Message = "The held delegation grant names a different user or organization than the current context; not chaining it.")]
    public static partial void HeldGrantUserMismatch(ILogger logger);
}
