namespace Blocks.Genesis;

/// <summary>
/// Shared contract values for Blocks actions. OS owns the root catalog (<see cref="ActionsKey"/>), the tenant's
/// switched-on actions (<see cref="ActionKey"/>) and <see cref="WebhooksKey"/>; Genesis reads the tenant entry of one
/// action and sends one <see cref="BlocksAction"/> per enabled target queue.
/// </summary>
public static class ActionConstants
{
    /// <summary>Root KeyValueStores key of the action catalog: a list of every action, all flags off.</summary>
    public const string ActionsKey = "actions";

    /// <summary>
    /// Key prefix of a tenant's switched-on actions: one entry per action (<see cref="ActionKey"/>), holding a copy
    /// of the catalog entry with its flags. No entry means the action is off for every target.
    /// </summary>
    public const string ActionKeyPrefix = "actions:";

    /// <summary>Tenant KeyValueStores key of one action, for example <c>actions:iam.user.created</c>.</summary>
    public static string ActionKey(string name) => ActionKeyPrefix + name;

    /// <summary>KeyValueStores key of the tenant's webhook list (owned by OS).</summary>
    public const string WebhooksKey = "webhooks";

    /// <summary>Queue of actions whose <see cref="ActionDefinition.Workflow"/> flag is on.</summary>
    public const string WorkflowQueue = "blocks_logic_action_workflow_listener";

    /// <summary>Queue of actions whose <see cref="ActionDefinition.Function"/> flag is on.</summary>
    public const string FunctionQueue = "blocks_logic_action_function_listener";

    /// <summary>Queue of actions whose <see cref="ActionDefinition.Webhook"/> flag is on.</summary>
    public const string WebhookQueue = "blocks_os_action_webhook_listener";

    /// <summary>Upper bound, in UTF-8 bytes, of a serialized action envelope. Larger actions are dropped.</summary>
    public const int MaxMessageBytes = 64 * 1024;
}
