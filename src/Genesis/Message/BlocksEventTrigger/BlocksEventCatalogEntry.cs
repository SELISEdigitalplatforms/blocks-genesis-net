using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis;

/// <summary>
/// One event in a tenant's Blocks Event Trigger catalog. The catalog is the array stored under
/// <see cref="BlocksEventTriggerConstants.CatalogKey"/> in the tenant DB's <c>KeyValueStores</c>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class BlocksEventCatalogEntry
{
    /// <summary>The exact wire <c>Type</c> (C# class name). Matched ordinally and case-sensitively.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>Display only.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Display only.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Primary producing service key (for example <c>iam</c>).</summary>
    public string ServiceKey { get; set; } = string.Empty;

    /// <summary>Every producing service key.</summary>
    public string[] SourceServiceKeys { get; set; } = [];

    /// <summary>Informational. Genesis does not match on it.</summary>
    public string[] ConsumerNames { get; set; } = [];

    /// <summary>Genesis forwards the event only when this is <c>true</c>.</summary>
    public bool IsValidForWorkflow { get; set; }

    /// <summary>Stored for function triggers; no runtime effect yet.</summary>
    public bool IsValidForFunction { get; set; }
}
