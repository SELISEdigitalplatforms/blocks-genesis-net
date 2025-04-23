namespace Blocks.Genesis
{
    public interface IMessageClient
    {
        Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage, bool isExchange = false) where T : class;
        Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class;
    }
}
