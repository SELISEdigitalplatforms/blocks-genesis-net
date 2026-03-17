namespace Blocks.Genesis
{
    public interface IConsumer<in T>
    {
        Task Consume(T context);
    }
}
