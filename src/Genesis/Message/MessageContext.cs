namespace Blocks.Genesis;

/// <summary>
/// Ambient information about the message a consumer is currently handling.
/// Set only while a consumer runs and cleared afterwards.
/// </summary>
public static class MessageContext
{
    private static readonly AsyncLocal<string?> _queue = new();

    /// <summary>
    /// The queue (or topic) the current message was consumed from, or null when unknown
    /// or when called outside a consumer.
    /// </summary>
    public static string? Queue => _queue.Value;

    internal static void SetQueue(string? queue) => _queue.Value = queue;
}
