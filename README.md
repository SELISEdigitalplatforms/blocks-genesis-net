# l0-net-blocks-genesis

## Overview

This repository contains the core components for the l0-net-blocks-genesis project. The `src/Genesis` directory is organized into several modules, each responsible for a specific aspect of the system.

## Interface Methods and Usage

Below is a summary of all interfaces in `src/Genesis`, their methods, and example usage patterns:

### ICacheClient

Provides synchronous and asynchronous methods for interacting with a Redis cache, including string and hash operations, and pub/sub.

**Key Methods:**

- `IDatabase CacheDatabase()`
- `bool KeyExists(string key)` / `Task<bool> KeyExistsAsync(string key)`
- `bool AddStringValue(string key, string value[, long keyLifeSpan])` / `Task<bool> AddStringValueAsync(...)`
- `string GetStringValue(string key)` / `Task<string> GetStringValueAsync(string key)`
- `bool RemoveKey(string key)` / `Task<bool> RemoveKeyAsync(string key)`
- `bool AddHashValue(string key, IEnumerable<HashEntry> value[, long keyLifeSpan])` / `Task<bool> AddHashValueAsync(...)`
- `HashEntry[] GetHashValue(string key)` / `Task<HashEntry[]> GetHashValueAsync(string key)`
- `Task<long> PublishAsync(string channel, string message)`
- `Task SubscribeAsync(string channel, Action<RedisChannel, RedisValue> handler)`
- `Task UnsubscribeAsync(string channel)`

**Usage:**

```csharp
var exists = await cacheClient.KeyExistsAsync("myKey");
await cacheClient.AddStringValueAsync("myKey", "value", 3600);
```

### IBlocksSecret

Holds configuration secrets for various services.

**Properties:**

- Connection strings, database names, SSH credentials, and flags (see interface for full list).

**Usage:**

```csharp
string cacheConn = blocksSecret.CacheConnectionString;
```

### IProjectKey

Represents a project key property.

**Property:**

- `string ProjectKey { get; set; }`

### IDbContextProvider

Provides MongoDB database and collection access.

**Key Methods:**

- `IMongoDatabase GetDatabase(string tenantId)`
- `IMongoDatabase? GetDatabase()`
- `IMongoDatabase GetDatabase(string connectionString, string databaseName)`
- `IMongoCollection<T> GetCollection<T>(string collectionName)`
- `IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName)`

**Usage:**

```csharp
var db = dbContextProvider.GetDatabase("tenant1");
var collection = dbContextProvider.GetCollection<MyEntity>("entities");
```

### IGrpcClientFactory

Creates gRPC clients for a given address.

**Method:**

- `TClient CreateGrpcClient<TClient>(string address) where TClient : ClientBase<TClient>`

### IConsumer<T>

Consumes a message or context of type T.

**Method:**

- `Task Consume(T context)`

### IMessageClient

Sends messages to consumers.

**Methods:**

- `Task SendToConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class`
- `Task SendToMassConsumerAsync<T>(ConsumerMessage<T> consumerMessage) where T : class`

### IRabbitMqService

RabbitMQ service abstraction.

**Properties/Methods:**

- `IChannel RabbitMqChannel { get; }`
- `Task CreateConnectionAsync()`
- `Task InitializeSubscriptionsAsync()`

### ITenants

Tenant management operations.

**Key Methods:**

- `Tenant? GetTenantByID(string tenantId)`
- `Tenant? GetTenantByApplicationDomain(string appName)`
- `Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings()`
- `(string?, string?) GetTenantDatabaseConnectionString(string tenantId)`
- `JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId)`
- `Task UpdateTenantVersionAsync()`

### ICryptoService

Hashing utilities.

**Methods:**

- `string Hash(string value, string salt)`
- `string Hash(byte[] value, bool makeBase64 = false)`

### IHttpService

HTTP client abstraction for RESTful operations.

**Key Methods:**

- `Task<(T, string)> Get<T>(...)`
- `Task<(T, string)> Post<T>(...)`
- `Task<(T, string)> Put<T>(...)`
- `Task<(T, string)> Delete<T>(...)`
- `Task<(T, string)> Patch<T>(...)`
- `Task<(T, string)> SendRequest<T>(...)`
- `Task<(T, string)> PostFormUrlEncoded<T>(...)`
- `Task<(T, string)> SendFormUrlEncoded<T>(...)`

### IVault

Secret management abstraction.

**Method:**

- `Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys)`

---

For more details, refer to the source code in each interface file under `src/Genesis`.