using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// <see cref="DataAccessResolver.ResolveWriteAsync"/> with the rules added by the validation and access rules plan,
/// against a real MongoDB (localhost:27017): "only when" conditions (phase 3), the reference filter and
/// Must not exist in (phase 4). Kept apart from <see cref="ResolveWriteTests"/>, which must stay unmodified (G1).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ResolveWriteNewRulesTests : IDisposable
{
    private readonly MongoClient _client = new("mongodb://127.0.0.1:27017");
    private readonly IMongoDatabase _db;
    private readonly DataAccessResolver _resolver;

    public ResolveWriteNewRulesTests()
    {
        _db = _client.GetDatabase("resolvewritenew_" + Guid.NewGuid().ToString("N"));
        var contextProvider = new Mock<IDbContextProvider>();
        contextProvider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
        var principals = new Mock<IAccessPrincipalAccessor>();
        principals.Setup(p => p.Current).Returns(User());
        _resolver = new DataAccessResolver(contextProvider.Object, new DataAccessPolicyResolutionService(new SchemaPolicySource()), principals.Object,
            new DataAccessOptions { SystemEntitiesOnly = false }, new Mock<ILogger<DataAccessResolver>>().Object);

        _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertMany(
        [
            Schema("Invoice", "Status", "Note", "CustomerId", "OtherCustomerId", "Email", "Tags"),
            Schema("Customer", "Name", "Status", "Email"),
            Schema("Blocklist", "Email", "Reason"),
        ]);
        _db.GetCollection<BsonDocument>("Customers").InsertMany(
        [
            new BsonDocument { ["_id"] = "c1", ["Status"] = "Active", ["Email"] = "one@x.io" },
            new BsonDocument { ["_id"] = "c2", ["Status"] = "Inactive", ["Email"] = "two@x.io" },
            new BsonDocument { ["_id"] = "c3", ["Status"] = "Active", ["IsDeleted"] = true },
        ]);
        _db.GetCollection<BsonDocument>("Blocklists").InsertMany(
        [
            new BsonDocument { ["_id"] = "b1", ["Email"] = "spam@x.io", ["Reason"] = "Spam" },
            new BsonDocument { ["_id"] = "b2", ["Email"] = "old@x.io", ["Reason"] = "Spam", ["IsDeleted"] = true },
            new BsonDocument { ["_id"] = "b3", ["Email"] = "test@x.io", ["Reason"] = "Test" },
        ]);
    }

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private static SchemaDefinition Schema(string name, params string[] fields) => new()
    {
        ItemId = $"sch-{name}",
        SchemaName = name,
        CollectionName = name + "s",
        SchemaType = SchemaType.Entity,
        Fields = fields.Select(f => new FieldDefinition { Name = f, Type = "String", IsArray = f == "Tags" }).ToList(),
    };

    private void Rule(string field, ValidationRule rule) =>
        _db.GetCollection<DataValidation>("DataValidations").InsertOne(new DataValidation
        {
            ItemId = Guid.NewGuid().ToString(), SchemaId = "sch-Invoice", FieldName = field, Validations = [rule],
        });

    private static PolicyRuleGroup Where(string field, PolicyOperator op, object? value) => new()
    {
        Rules = [new() { LeftOperand = field, Operator = op, RightSource = ConditionSource.STATIC_VALUE, StaticValue = value }],
    };

    private static ValidationRule ActiveCustomer(int version = 2) => new()
    {
        Type = ValidationType.Reference, Value = "Customer", EvaluationVersion = version, TargetFilter = Where("Status", PolicyOperator.EQUAL, "Active"),
    };

    private static ValidationRule NotBlocked(PolicyRuleGroup? filter = null) => new()
    {
        Type = ValidationType.NotExistsIn, Value = "Blocklist", SecondaryValue = "Email", TargetFilter = filter,
    };

    private Task<List<ValidationError>> Insert(BsonDocument document) =>
        _resolver.ResolveWriteAsync("Invoice", document, isInsert: true).ContinueWith(t => t.Result.Validation.Errors);

    // ── "Only when" (phase 3) ──

    [Fact]
    public async Task ConditionalRule_SeesAPropertyClearedInTheRawDocument()
    {
        // ToRecord() drops the null, so the raw document is what tells the rule the note was sent, and cleared.
        Rule("Note", new ValidationRule { Type = ValidationType.NotEmpty, EvaluationVersion = 1, When = Where("Status", PolicyOperator.EQUAL, "Open") });
        var clearNote = new BsonDocument("Note", BsonNull.Value);

        var write = await _resolver.ResolveWriteAsync("Invoice", clearNote, isInsert: false,
            storedRecord: new BsonDocument { ["_id"] = "i1", ["Status"] = "Open", ["Note"] = "Hello" });

        Assert.Equal("Note", Assert.Single(write.Validation.Errors).FieldName);
    }

    // ── Reference with a filter (phase 4) ──

    [Fact]
    public async Task Reference_WithAFilter_RefusesATargetRowThatDoesNotMatchIt()
    {
        Rule("CustomerId", ActiveCustomer());

        Assert.Empty(await Insert(new BsonDocument("CustomerId", "c1")));
        var error = Assert.Single(await Insert(new BsonDocument("CustomerId", "c2")));
        Assert.Equal(("CustomerId", "Reference"), (error.FieldName, error.ValidationType));
        Assert.Equal("Value 'c2' of Invoice.CustomerId was not found in Customer.ItemId.", error.Message);
    }

    [Fact]
    public async Task Reference_AFilterOnItemId_ReadsTheStoredId()
    {
        Rule("Email", new ValidationRule
        {
            Type = ValidationType.Reference, Value = "Customer", SecondaryValue = "Email", EvaluationVersion = 2,
            TargetFilter = Where("ItemId", PolicyOperator.NOT_EQUAL, "c1"),
        });

        Assert.Single(await Insert(new BsonDocument("Email", "one@x.io")));
        Assert.Empty(await Insert(new BsonDocument("Email", "two@x.io")));
    }

    [Fact]
    public async Task Reference_Version2_SkipsDeletedRows()
    {
        Rule("CustomerId", new ValidationRule { Type = ValidationType.Reference, Value = "Customer", EvaluationVersion = 2 });
        Assert.Single(await Insert(new BsonDocument("CustomerId", "c3")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Reference_BelowVersion2_StillCountsDeletedRows(int version)
    {
        // G4: older rules keep today's lookup.
        Rule("CustomerId", new ValidationRule { Type = ValidationType.Reference, Value = "Customer", EvaluationVersion = version });
        Assert.Empty(await Insert(new BsonDocument("CustomerId", "c3")));
    }

    [Fact]
    public async Task RulesWithDifferentFilters_DoNotShareALookup()
    {
        // Same target, one rule filtered, one not: each answers for itself.
        Rule("CustomerId", ActiveCustomer());
        Rule("OtherCustomerId", new ValidationRule { Type = ValidationType.Reference, Value = "Customer", EvaluationVersion = 1 });

        var error = Assert.Single(await Insert(new BsonDocument { ["CustomerId"] = "c2", ["OtherCustomerId"] = "c2" }));
        Assert.Equal("CustomerId", error.FieldName);
    }

    [Fact]
    public async Task Reference_WithAFilterOnAPropertyTheTargetDoesNotHave_IsLeftUnchecked()
    {
        // Logged and left unchecked, like a missing target: a publish refuses it (ReferenceLookupTests).
        Rule("CustomerId", new ValidationRule { Type = ValidationType.Reference, Value = "Customer", EvaluationVersion = 2, TargetFilter = Where("Missing", PolicyOperator.EQUAL, "x") });
        Assert.Empty(await Insert(new BsonDocument("CustomerId", "nobody")));
    }

    // ── Must not exist in (phase 4) ──

    [Fact]
    public async Task MustNotExistIn_RefusesAValueFoundInTheTarget()
    {
        Rule("Email", NotBlocked());

        Assert.Empty(await Insert(new BsonDocument("Email", "fine@x.io")));
        var error = Assert.Single(await Insert(new BsonDocument("Email", "spam@x.io")));
        Assert.Equal(("Email", "NotExistsIn"), (error.FieldName, error.ValidationType));
        Assert.Equal("Value 'spam@x.io' of Invoice.Email already exists in Blocklist.Email.", error.Message);
    }

    [Fact]
    public async Task MustNotExistIn_IgnoresDeletedRows()
    {
        Rule("Email", NotBlocked());
        Assert.Empty(await Insert(new BsonDocument("Email", "old@x.io")));
    }

    [Fact]
    public async Task MustNotExistIn_WithAFilter_OnlyCountsMatchingRows()
    {
        Rule("Email", NotBlocked(Where("Reason", PolicyOperator.EQUAL, "Spam")));

        Assert.Empty(await Insert(new BsonDocument("Email", "test@x.io")));
        Assert.Single(await Insert(new BsonDocument("Email", "spam@x.io")));
    }

    [Fact]
    public async Task MustNotExistIn_ChecksEachListItem()
    {
        Rule("Tags", new ValidationRule { Type = ValidationType.NotExistsIn, Value = "Blocklist", SecondaryValue = "Email" });

        var error = Assert.Single(await Insert(new BsonDocument("Tags", new BsonArray { "a@x.io", "spam@x.io", "old@x.io" })));
        Assert.Equal("Tags[1]", error.FieldName);
    }

    [Fact]
    public async Task MustNotExistIn_ACustomMessageWins()
    {
        var rule = NotBlocked();
        rule.ErrorMessage = "This address is blocked";
        Rule("Email", rule);

        Assert.Equal("This address is blocked", Assert.Single(await Insert(new BsonDocument("Email", "spam@x.io"))).Message);
    }

    [Fact]
    public async Task ReferenceAndMustNotExistIn_OnOneField_BothApply()
    {
        // Must be a customer's address, and not a blocked one.
        _db.GetCollection<BsonDocument>("Blocklists").InsertOne(new BsonDocument { ["_id"] = "b4", ["Email"] = "two@x.io", ["Reason"] = "Spam" });
        _db.GetCollection<DataValidation>("DataValidations").InsertOne(new DataValidation
        {
            ItemId = "v-email", SchemaId = "sch-Invoice", FieldName = "Email",
            Validations =
            [
                new ValidationRule { Type = ValidationType.Reference, Value = "Customer", SecondaryValue = "Email", EvaluationVersion = 2 },
                NotBlocked(),
            ],
        });

        Assert.Empty(await Insert(new BsonDocument("Email", "one@x.io")));
        Assert.Equal("NotExistsIn", Assert.Single(await Insert(new BsonDocument("Email", "two@x.io"))).ValidationType);
        Assert.Equal("Reference", Assert.Single(await Insert(new BsonDocument("Email", "new@x.io"))).ValidationType);
        Assert.Equal(2, (await Insert(new BsonDocument("Email", "spam@x.io"))).Count);   // blocked, and no customer's
    }

    [Fact]
    public async Task AFilterPatternMongoCantRun_RefusesTheValue_InsteadOfCrashingTheWrite()
    {
        // A .NET balancing group passes the save check's Regex parse, but MongoDB (PCRE) refuses it.
        Rule("Email", NotBlocked(new PolicyRuleGroup
        {
            Rules = [new() { LeftOperand = "Reason", Operator = PolicyOperator.REGEX, RightSource = ConditionSource.STATIC_VALUE, StaticValue = "(?<a>x)(?<-a>)" }],
        }));

        var error = Assert.Single(await Insert(new BsonDocument("Email", "spam@x.io")));
        Assert.Equal(("Email", DataValidationHelper.ConditionNotChecked), (error.FieldName, error.Message));
    }
}
