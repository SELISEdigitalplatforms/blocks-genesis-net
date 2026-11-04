namespace Blocks.Genesis;

/// <summary>
/// The message body Genesis sends for a published action. Tenant, user, trace and delegation travel in the
/// normal message properties, never in the body.
/// </summary>
public sealed class BlocksAction
{
    /// <summary><c>evt_</c> plus 32 hex characters, new per publish; the consumers' dedupe key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The action name.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>UTC time of the publish.</summary>
    public DateTime Time { get; set; }

    /// <summary>The organization, or <c>null</c> when there is none.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>A JSON object holding only the fields named in <see cref="ActionDefinition.Payload"/>.</summary>
    public string DataJson { get; set; } = "{}";
}
