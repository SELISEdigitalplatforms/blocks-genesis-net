using MongoDB.Driver;

namespace Blocks.Genesis
{
    public interface IDbContextProvider
    {
        IMongoDatabase GetDatabase(string databaseName);
        IMongoDatabase GetDatabase();
        IMongoDatabase GetDatabase(string connectionString, string databaseName);
        IMongoCollection<T> GetCollection<T>(string collectionName);
        IMongoCollection<T> GetCollection<T>(string databaseName, string collectionName);
    }
}
