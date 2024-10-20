using MongoDB.Driver;

namespace Blocks.Genesis
{
    public interface IDbContextProvider
    {
        IMongoDatabase GetDatabase(string tenantId);
        IMongoDatabase? GetDatabase();
        IMongoDatabase GetDatabase(string connectionString, string databaseName);
        IMongoCollection<T> GetCollection<T>(string collectionName);
        IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName);
    }
}
