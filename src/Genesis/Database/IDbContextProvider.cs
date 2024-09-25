using MongoDB.Driver;

namespace Blocks.Genesis
{
    public interface IDbContextProvider
    {
        IMongoDatabase GetDatabase(string tenantId);
        IMongoDatabase GetDatabase();
        IMongoDatabase GetDatabase(string connectionString, string databaseName);
        IMongoCollection<T> GetCollection<T>(string collectionName);
        IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName);
        public T RunMongoCommandWithActivity<T>(string collectionName, string action, Func<T> mongoCommand);
        public Task<T> RunMongoCommandWithActivityAsync<T>(string collectionName, string action, Func<Task<T>> mongoCommand);
    }
}
