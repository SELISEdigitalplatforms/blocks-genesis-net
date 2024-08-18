using MongoDB.Driver;

namespace Blocks.Genesis
{
    public interface IDbContextProvider
    {
        Task<IMongoDatabase> GetDatabase(string databaseName);
        Task<IMongoDatabase> GetDatabase();
        IMongoDatabase GetDatabase(string connectionString, string databaseName);
        Task<IMongoCollection<T>> GetCollection<T>(string collectionName);
        Task<IMongoCollection<T>> GetCollection<T>(string databaseName, string collectionName);
    }
}
