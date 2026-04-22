namespace Blocks.Genesis;

public class BlocksException : Exception
{
    public BlocksException()
    {
    }

    public BlocksException(string message) : base(message)
    {
    }

    public BlocksException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
