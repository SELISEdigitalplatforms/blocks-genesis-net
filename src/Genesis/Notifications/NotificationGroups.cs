namespace Blocks.Genesis.Notifications;

/// <summary>SignalR group names shared by the senders (genesis) and the hub (logic). A contract: never change the format.</summary>
public static class NotificationGroups
{
    /// <summary>Every connection of a tenant (broadcast).</summary>
    public static string ForTenant(string tenantId) => $"tenant:{tenantId}";

    /// <summary>One user's connections in a tenant (project users, and root console users working in the project).</summary>
    public static string ForTenantUser(string tenantId, string userId) => $"tenant:{tenantId}:user:{userId}";

    /// <summary>Every connection of one organization in a tenant ("All users" level).</summary>
    public static string ForTenantOrg(string tenantId, string organizationId) => $"tenant:{tenantId}:org:{organizationId}";
}
