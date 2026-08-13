using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace XUnitTest.KeyValueStore;

public sealed class KeyValueStoreTests
{
    [Fact]
    public void AddKeyValueStore_RegistersMongoImplementation()
    {
        var services = new ServiceCollection();

        var returned = services.AddKeyValueStore();

        Assert.Same(services, returned);
        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IKeyValueStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(MongoKeyValueStore), descriptor.ImplementationType);
    }

    [Fact]
    public void KeyValueEntry_SerializesWithExpectedMongoFieldNames()
    {
        var entry = new KeyValueEntry
        {
            Key = "email.settings",
            Value = new
            {
                SenderName = "My Application",
                RetryCount = 3,
                Options = new
                {
                    Enabled = true,
                    Timeout = 30
                }
            }.ToBsonDocument(),
            CreatedDate = DateTime.SpecifyKind(new DateTime(2026, 8, 12, 10, 0, 0), DateTimeKind.Utc),
            CreatedBy = "user-123",
            LastUpdatedDate = DateTime.SpecifyKind(new DateTime(2026, 8, 12, 11, 0, 0), DateTimeKind.Utc),
            LastUpdatedBy = "user-456"
        };

        var document = entry.ToBsonDocument();

        Assert.Contains("Key", document.Names);
        Assert.Contains("Value", document.Names);
        Assert.Contains("CreatedDate", document.Names);
        Assert.Contains("CreatedBy", document.Names);
        Assert.Contains("LastUpdatedDate", document.Names);
        Assert.Contains("LastUpdatedBy", document.Names);
        Assert.Contains("OrganizationId", document.Names);
        Assert.Contains("Tags", document.Names);
        Assert.Equal("email.settings", document["Key"].AsString);
        Assert.Equal("My Application", document["Value"]["SenderName"].AsString);
        Assert.Equal(3, document["Value"]["RetryCount"].AsInt32);
        Assert.True(document["Value"]["Options"]["Enabled"].AsBoolean);
    }

    [Fact]
    public void KeyValueEntry_ValueCanDeserializeToStructuredType()
    {
        var value = new EmailSettings(
            "My Application",
            3,
            new EmailOptions(true, 30));
        var entry = new KeyValueEntry
        {
            Key = "email.settings",
            Value = value.ToBsonDocument()
        };

        var result = BsonSerializer.Deserialize<EmailSettings>(entry.Value.ToJson());

        Assert.Equal(value.SenderName, result.SenderName);
        Assert.Equal(value.RetryCount, result.RetryCount);
        Assert.Equal(value.Options.Enabled, result.Options.Enabled);
        Assert.Equal(value.Options.Timeout, result.Options.Timeout);
    }

    private sealed record EmailSettings(string SenderName, int RetryCount, EmailOptions Options);

    private sealed record EmailOptions(bool Enabled, int Timeout);
}
