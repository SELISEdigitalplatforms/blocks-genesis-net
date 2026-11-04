namespace Blocks.Genesis;

/// <summary>
/// Shared contract values for Blocks actions. OS owns the lists stored under <see cref="ActionsKey"/> and
/// <see cref="WebhooksKey"/>; Genesis reads the tenant's actions and sends one <see cref="BlocksAction"/> per
/// enabled target queue.
/// </summary>
public static class ActionConstants
{
    /// <summary>KeyValueStores key: the root holds the master list, each tenant a copy with its flags.</summary>
    public const string ActionsKey = "actions";

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
