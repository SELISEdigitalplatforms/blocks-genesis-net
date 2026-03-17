using RabbitMQ.Client;

namespace Blocks.Genesis
{
    public interface IRabbitMqService
    {
        IChannel RabbitMqChannel { get; }
        Task CreateConnectionAsync();
        Task InitializeSubscriptionsAsync();
    }
}
