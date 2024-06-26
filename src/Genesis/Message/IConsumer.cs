namespace Blocks.Genesis
{
    public interface IConsumer<T>
    {
        Task Consume(T context);
    }
}
