using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis;

/// <summary>
/// One action in the list stored under <see cref="ActionConstants.ActionsKey"/>. The root list is the master;
/// each tenant keeps a copy whose flags say where the action is sent.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ActionDefinition
{
    /// <summary>The action name a service passes to <see cref="IActionPublisher.PublishAsync"/>. Matched ordinally.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display only.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Producing service key (for example <c>iam</c>).</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Field name to type text (for example <c>string</c>, <c>string[]</c>, <c>boolean</c>). Only these top-level
    /// fields of the published data are sent; names are matched case-sensitively.
    /// </summary>
    public Dictionary<string, string> Payload { get; set; } = [];

    /// <summary>Send to <see cref="ActionConstants.WorkflowQueue"/>.</summary>
    public bool Workflow { get; set; }

    /// <summary>Send to <see cref="ActionConstants.FunctionQueue"/>.</summary>
    public bool Function { get; set; }

    /// <summary>Send to <see cref="ActionConstants.WebhookQueue"/>.</summary>
    public bool Webhook { get; set; }
}
