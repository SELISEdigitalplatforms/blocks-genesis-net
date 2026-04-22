namespace Blocks.Genesis;

public sealed class BlocksValidationException : BlocksException
{
    public IDictionary<string, string[]> Errors { get; }

    public BlocksValidationException(string message, IDictionary<string, string[]> errors) : base(message)
    {
        Errors = errors ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }

    public BlocksValidationException(string message, IDictionary<string, string[]> errors, Exception innerException)
        : base(message, innerException)
    {
        Errors = errors ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }
}
