using System.Diagnostics;

namespace Blocks.Genesis;

/// <summary>
/// Forwards catalogued events to the Blocks Event Trigger queue. The message clients call it after an
/// original send completes. Registered by <c>ApplicationConfigurations.ConfigureServices</c>; services
/// do not call it themselves.
/// </summary>
public interface IBlocksEventTriggerForwarder
{
    /// <summary>
    /// Best-effort: forwards the original when the sending tenant enabled its wire type for workflows.
    /// Never throws.
    /// </summary>
    /// <param name="original">The original message, after its send completed.</param>
    /// <param name="wireType">The original envelope's <c>Type</c>.</param>
    /// <param name="payloadJson">The original envelope's <c>Body</c>.</param>
    /// <param name="tenantId">The sending tenant, from the same context the original used.</param>
    /// <param name="isMassConsumer"><c>true</c> for a topic or exchange send.</param>
    /// <param name="producerActivity">The original's producer span, tagged with the outcome.</param>
    /// <param name="sendForward">
    /// Sends the forward on the same client: the message carries the routing and transport fields, the
    /// string is the serialized envelope. It must let failures surface.
    /// </param>
    ValueTask TryForwardAsync<T>(
        ConsumerMessage<T> original,
        string wireType,
        string payloadJson,
        string? tenantId,
        bool isMassConsumer,
        Activity? producerActivity,
        Func<ConsumerMessage<BlocksEventTriggerMessage>, string, Task> sendForward) where T : class;
}
