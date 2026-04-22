namespace Blocks.Genesis;

public sealed class BlocksAuthenticationException : BlocksException
{
    public BlocksAuthenticationException(string message) : base(message)
    {
    }

    public BlocksAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
