using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using Blocks.Genesis.Entities.Os;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// <see cref="DataAccessResolver.ResolveWriteAsync"/> for system schemas, against a real MongoDB
/// (localhost:27017): validation rules and defaults stored like the OS Data Access page stores them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ResolveWriteTests : IDisposable
{
    private readonly MongoClient _client = new("mongodb://127.0.0.1:27017");
    private readonly IMongoDatabase _db;
    private readonly DataAccessResolver _resolver;
    private readonly Mock<ILogger<DataAccessResolver>> _logger = new();

    public ResolveWriteTests()
    {
        _db = _client.GetDatabase("resolvewrite_" + Guid.NewGuid().ToString("N"));
        var contextProvider = new Mock<IDbContextProvider>();
        contextProvider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
        var principals = new Mock<IAccessPrincipalAccessor>();
        principals.Setup(p => p.Current).Returns(User());
        _resolver = new DataAccessResolver(contextProvider.Object, new DataAccessPolicyResolutionService(new SchemaPolicySource()), principals.Object,
            new DataAccessOptions { SystemEntitiesOnly = true }, _logger.Object);

        _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertMany(
        [
            SystemSchema("EmailTemplate", "Name", "TemplateSubject", "TemplateBody", "MailConfigurationId", "Tags"),
            SystemSchema("Role", "Name", "Slug"),
            SystemSchema("RefreshTokenModel", "TokenId", "Scope"),
        ]);
        _db.GetCollection<BsonDocument>("Roles").InsertMany([new BsonDocument("Slug", "admin"), new BsonDocument("Slug", "hr")]);
    }

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private static SchemaDefinition SystemSchema(string name, params string[] fields) => new()
    {
        ItemId = $"sch-{name}",
        SchemaName = name,
        CollectionName = name + "s",
        SchemaType = SchemaType.Entity,
        IsSystemEntity = true,
        Fields = fields.Select(f => new FieldDefinition { Name = f, Type = "String", IsArray = f == "Tags" }).ToList(),
    };

    private void Rule(string schema, string field, ValidationRule rule) =>
        _db.GetCollection<DataValidation>("DataValidations").InsertOne(new DataValidation
        {
            ItemId = Guid.NewGuid().ToString(), SchemaId = $"sch-{schema}", FieldName = field, Validations = [rule],
        });

    private void Default(string schema, string field, FieldDefaultValue value)
    {
        var schemas = _db.GetCollection<SchemaDefinition>("SchemaDefinitions");
        var definition = schemas.Find(s => s.ItemId == $"sch-{schema}").Single();
        definition.Fields.Single(f => f.Name == field).DefaultValue = value;
        schemas.ReplaceOne(s => s.ItemId == definition.ItemId, definition);
    }

    private static BsonDocument Template(string? subject = null) =>
        new EmailTemplate { ItemId = "t1", Name = "Welcome", TemplateSubject = subject, TemplateBody = "Hi" }.ToBsonDocument();

    [Fact]
    public async Task NothingConfigured_DocumentComesBackUnchanged()
    {
        var document = Template("Hello");
        var before = document.DeepClone();

        var write = await _resolver.ResolveWriteAsync("EmailTemplate", document, isInsert: true);

        Assert.True(write.IsValid);
        Assert.False(write.DefaultsApplied);
        Assert.Equal(before, write.Document);
    }

    [Fact]
    public async Task BrokenRule_IsReported()
    {
        Rule("EmailTemplate", "TemplateSubject", new ValidationRule { Type = ValidationType.MaxLength, Value = 5, ErrorMessage = "Subject too long" });

        var write = await _resolver.ResolveWriteAsync("EmailTemplate", Template("Far too long"), isInsert: false);

        var error = Assert.Single(write.Validation.Errors);
        Assert.Equal(("TemplateSubject", "Subject too long"), (error.FieldName, error.Message));
    }

    [Fact]
    public async Task Default_FillsANullProperty_OnInsertOnly_AndNeverOverwrites()
    {
        Default("EmailTemplate", "TemplateSubject", new FieldDefaultValue { Kind = DefaultValueKind.Static, Value = "Hello" });

        var inserted = await _resolver.ResolveWriteAsync("EmailTemplate", Template(), isInsert: true);
        Assert.True(inserted.DefaultsApplied);
        Assert.Equal("Hello", inserted.Document["TemplateSubject"].AsString);

        var updated = await _resolver.ResolveWriteAsync("EmailTemplate", Template(), isInsert: false);
        Assert.True(updated.Document["TemplateSubject"].IsBsonNull);

        var kept = await _resolver.ResolveWriteAsync("EmailTemplate", Template("Mine"), isInsert: true);
        Assert.Equal("Mine", kept.Document["TemplateSubject"].AsString);
    }

    [Fact]
    public async Task NullProperty_IsNotValidated_LikeAValueTheGatewayNeverReceived()
    {
        Rule("EmailTemplate", "TemplateSubject", new ValidationRule { Type = ValidationType.MinLength, Value = 3 });

        Assert.True((await _resolver.ResolveWriteAsync("EmailTemplate", Template(), isInsert: false)).IsValid);
    }

    [Fact]
    public async Task RulesAndDefaultsOnBlockedProperties_AreIgnored()
    {
        // MailConfigurationId isn't opened by the allow-list; RefreshTokenModel is excluded entirely.
        Rule("EmailTemplate", "MailConfigurationId", new ValidationRule { Type = ValidationType.NotEmpty, Value = null });
        Default("EmailTemplate", "MailConfigurationId", new FieldDefaultValue { Kind = DefaultValueKind.Static, Value = "cfg" });
        Rule("RefreshTokenModel", "Scope", new ValidationRule { Type = ValidationType.MaxLength, Value = 1 });

        var template = await _resolver.ResolveWriteAsync("EmailTemplate", Template("Hi"), isInsert: true);
        var token = await _resolver.ResolveWriteAsync("RefreshTokenModel", new BsonDocument { ["TokenId"] = "x", ["Scope"] = "openid profile" }, isInsert: true);

        Assert.True(template.IsValid);
        Assert.True(template.Document["MailConfigurationId"].IsBsonNull);
        Assert.True(token.IsValid);
    }

    [Fact]
    public async Task AnIgnoredDefault_IsLogged()
    {
        // TemplateBody takes validation rules but no default.
        Default("EmailTemplate", "TemplateBody", new FieldDefaultValue { Kind = DefaultValueKind.Static, Value = "Hello" });
        var document = Template("Hi");
        document["TemplateBody"] = BsonNull.Value;

        var write = await _resolver.ResolveWriteAsync("EmailTemplate", document, isInsert: true);

        Assert.True(write.Document["TemplateBody"].IsBsonNull);
        _logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("EmailTemplate.TemplateBody default")), null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task AVersionZeroRuleOnAList_StillChecksTheListAsAWhole()
    {
        var document = Template("Hi");
        document["Tags"] = new BsonArray { "a", "b" };

        // Version 0 (saved before item-by-item checks existed): the list as a whole, as the gateway always did.
        Rule("EmailTemplate", "Tags", new ValidationRule { Type = ValidationType.MaxLength, Value = 5 });
        var legacy = await _resolver.ResolveWriteAsync("EmailTemplate", document, isInsert: false);
        Assert.Equal("Tags", Assert.Single(legacy.Validation.Errors).FieldName);

        // Version 1: each item, and both fit.
        _db.GetCollection<DataValidation>("DataValidations").DeleteMany(FilterDefinition<DataValidation>.Empty);
        Rule("EmailTemplate", "Tags", new ValidationRule { Type = ValidationType.MaxLength, Value = 5, EvaluationVersion = 1 });
        Assert.True((await _resolver.ResolveWriteAsync("EmailTemplate", document, isInsert: false)).IsValid);
    }

    [Fact]
    public async Task Reference_EachTagMustBeAnExistingRoleSlug()
    {
        Rule("EmailTemplate", "Tags", new ValidationRule { Type = ValidationType.Reference, Value = "Role", SecondaryValue = "Slug", EvaluationVersion = 1 });
        var document = Template("Hi");
        document["Tags"] = new BsonArray { "admin", "ghost", "hr" };

        var write = await _resolver.ResolveWriteAsync("EmailTemplate", document, isInsert: true);

        var error = Assert.Single(write.Validation.Errors);
        Assert.Equal(("Tags[1]", "Reference"), (error.FieldName, error.ValidationType));
        Assert.Equal("Value 'ghost' of EmailTemplate.Tags[1] was not found in Role.Slug.", error.Message);
    }

    [Fact]
    public async Task UnknownSchema_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _resolver.ResolveWriteAsync("Nope", new BsonDocument(), isInsert: true));
    }
}
