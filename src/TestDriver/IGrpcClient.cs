namespace TestDriver
{
    public interface IGrpcClient
    {
        Task<HelloReply?> ExecuteAsync();
    }
}
