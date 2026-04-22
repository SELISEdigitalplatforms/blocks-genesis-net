namespace Blocks.Genesis;

public sealed class BlocksNotFoundException : BlocksException
{
    public BlocksNotFoundException(string message) : base(message)
    {
    }

    public BlocksNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
