namespace Blocks.Genesis;

public sealed class BlocksRateLimitException : BlocksException
{
    public BlocksRateLimitException(string message) : base(message)
    {
    }

    public BlocksRateLimitException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
