namespace Blocks.Genesis.Notifications;

/// <summary>Which organization a send is for (spec: "org always comes from the send").</summary>
internal static class SendOrganization
{
    /// <summary>
    /// A user-token send of the same tenant uses the token's organization, whatever the request says: a caller must
    /// not reach another organization by naming it. Any other send (service, worker, other tenant) uses the request's.
    /// Null when there is none.
    /// </summary>
    public static string? Resolve(BlocksContext? context, string tenantId, string? requestOrganizationId)
    {
        var isUserTokenSend = context is { IsAuthenticated: true }
            && !string.IsNullOrWhiteSpace(context.UserId)
            && string.Equals(context.TenantId, tenantId, StringComparison.Ordinal);

        var organizationId = isUserTokenSend ? context!.OrganizationId : requestOrganizationId;
        return string.IsNullOrWhiteSpace(organizationId) ? null : organizationId.Trim();
    }
}
