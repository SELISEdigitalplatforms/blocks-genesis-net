using System.Diagnostics;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// Edge cases found in the review of phases 1–4 (validation and access rules plan): every one must refuse, check or
/// fail closed, never let a value through silently or crash a write.
/// </summary>
public class RuleEdgeCasesTests
{
    private static readonly IReadOnlyDictionary<string, VariableField> Fields = new Dictionary<string, VariableField>
    {
        ["Email"] = new("String", false, false),
        ["Country"] = new("String", false, false),
        ["Items"] = new("Item", true, true),
        ["Items.Qty"] = new("Int", false, false),
    };

    private static PolicyRuleGroup Where(string field, string value) => new()
    {
        Rules = [new() { LeftOperand = field, Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.STATIC_VALUE, StaticValue = value }],
    };

    private static IReadOnlyList<string> Check(ValidationRule rule, string path = "Email") =>
        ValidationRuleCheck.Check(rule, new RuleField(path, Fields[path].Type, Fields[path].IsArray), "Lead", false, Fields);

    // ── Save checks ──

    [Theory]
    [InlineData(ValidationType.Reference)]
    [InlineData(ValidationType.NotExistsIn)]
    public void ALookupRule_CantHaveACondition_YetItsFilterIsFine(ValidationType type)
    {
        Assert.Contains(ValidationRuleCheck.ConditionOnLookup, Check(new ValidationRule { Type = type, Value = "Customer", When = Where("Country", "DE") }));
        Assert.DoesNotContain(ValidationRuleCheck.ConditionOnLookup, Check(new ValidationRule { Type = type, Value = "Customer", TargetFilter = Where("Status", "Active") }));
    }

    [Fact]
    public void NoDuplicates_NeedsAListOfValues_ItemCountsWorkOnObjects()
    {
        Assert.Contains(ValidationRuleCheck.UniqueItemsNeedsValues, Check(new ValidationRule { Type = ValidationType.UniqueItems }, "Items"));
        Assert.Empty(Check(new ValidationRule { Type = ValidationType.MinItems, Value = 1L }, "Items"));
    }

    [Fact]
    public void AnEmptyGroup_IsRefused_InAConditionAndAFilter()
    {
        var withEmptyNested = Where("Country", "DE");
        withEmptyNested.NestedGroups.Add(new PolicyRuleGroup());
        Assert.Contains(ValidationRuleCheck.ConditionGroupEmpty, Check(new ValidationRule { Type = ValidationType.NotEmpty, When = withEmptyNested }));
        Assert.Contains(ValidationRuleCheck.ConditionGroupEmpty, Check(new ValidationRule { Type = ValidationType.NotEmpty, When = new PolicyRuleGroup() }));
        Assert.Contains(ValidationRuleCheck.ConditionGroupEmpty, Check(new ValidationRule { Type = ValidationType.Reference, Value = "Customer", TargetFilter = withEmptyNested }));
    }

    // ── Dates relative to now ──

    private static readonly DateTime At1400 = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);

    private static DateTime From(string text) => RelativeNow.TryParse(text, out var now) ? now.From(At1400) : throw new ArgumentException(text);

    [Fact]
    public void DaysMonthsAndYears_AreWholeUtcDays_HoursAndMinutesExact()
    {
        Assert.Equal(new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), From("{{Now+30d}}"));
        Assert.Equal(new DateTime(2008, 10, 25, 0, 0, 0, DateTimeKind.Utc), From("{{Now-18y}}"));
        Assert.Equal(new DateTime(2026, 10, 25, 16, 0, 0, DateTimeKind.Utc), From("{{Now+2h}}"));
        Assert.Equal(At1400, From("{{Now}}"));
        Assert.Equal(DateTimeKind.Utc, From("{{Now+30d}}").Kind);
    }

    [Fact]
    public void AnOffsetBeyondTheCalendar_IsClampedNotThrown()
    {
        Assert.Equal(DateTime.MaxValue, From("{{Now+9999y}}"));
        Assert.Equal(DateTime.MinValue, From("{{Now-9999y}}"));
    }

    private static SchemaDefinitionExtended Schema(string field, string type, bool isArray, ValidationRule rule, List<FieldDefinitionResponse>? children = null) => new()
    {
        SchemaName = "Lead",
        Fields =
        [
            new FieldDefinitionResponse
            {
                Name = field, Type = type, IsArray = isArray, Fields = children ?? [],
                ValidationRule = new DataValidation { FieldName = field, Validations = [rule] },
            },
        ],
    };

    [Fact]
    public void TheDateTheMessageNames_IsAccepted()
    {
        var schema = Schema("Due", "DateTime", false, new ValidationRule { Type = ValidationType.GreaterThanOrEqual, Value = "{{Now+30d}}", EvaluationVersion = 1 });
        Assert.True(new Dictionary<string, object?> { ["Due"] = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc) }.Validate(schema, null, At1400).IsValid);
        var error = Assert.Single(new Dictionary<string, object?> { ["Due"] = new DateTime(2026, 11, 23, 23, 59, 0, DateTimeKind.Utc) }.Validate(schema, null, At1400).Errors);
        Assert.Contains("(2026-11-24)", error.Message);
    }

    [Fact]
    public void AFarOffset_NeverCrashesAWrite()
    {
        var schema = Schema("Due", "DateTime", false, new ValidationRule { Type = ValidationType.LessThan, Value = "{{Now+9999y}}", EvaluationVersion = 1 });
        Assert.True(new Dictionary<string, object?> { ["Due"] = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }.Validate(schema, null, At1400).IsValid);
    }

    // ── List rules ──

    private static readonly List<FieldDefinitionResponse> ItemFields = [new FieldDefinitionResponse { Name = "Qty", Type = "Int" }];

    [Fact]
    public void ItemCounts_CheckAListOfObjects()
    {
        var oneItem = new Dictionary<string, object?> { ["Items"] = new List<object?> { new Dictionary<string, object?> { ["Qty"] = 1 } } };
        Assert.False(oneItem.Validate(Schema("Items", "Item", true, new ValidationRule { Type = ValidationType.MinItems, Value = 2L }, ItemFields)).IsValid);
        Assert.False(oneItem.Validate(Schema("Items", "Item", true, new ValidationRule { Type = ValidationType.MaxItems, Value = 0L }, ItemFields)).IsValid);
        Assert.True(oneItem.Validate(Schema("Items", "Item", true, new ValidationRule { Type = ValidationType.MaxItems, Value = 1L }, ItemFields)).IsValid);
    }

    [Fact]
    public void ItemCounts_OnAListOfObjects_CanHaveACondition()
    {
        var rule = new ValidationRule { Type = ValidationType.MinItems, Value = 1L, When = Where("Country", "DE") };
        var schema = Schema("Items", "Item", true, rule, ItemFields);
        schema.Fields.Add(new FieldDefinitionResponse { Name = "Country", Type = "String" });
        Assert.False(new Dictionary<string, object?> { ["Country"] = "DE", ["Items"] = new List<object?>() }.Validate(schema).IsValid);
        Assert.True(new Dictionary<string, object?> { ["Country"] = "AT", ["Items"] = new List<object?>() }.Validate(schema).IsValid);
    }

    [Fact]
    public void ItemCounts_StoredOnAListOfObjects_ReachTheCheck_NothingElseDoes()
    {
        // As stored: the list, and its item's property as a reference row.
        List<FieldDefinition> stored =
        [
            new() { Name = "Items", Type = "Item", IsArray = true },
            new() { Name = "Items.Qty", Type = "Int", IsReferenceField = true, ReferenceFieldType = "Item" },
            new() { Name = "Address", Type = "Address" },
            new() { Name = "Address.City", Type = "String", IsReferenceField = true, ReferenceFieldType = "Address" },
        ];
        List<DataValidation> validations =
        [
            new() { FieldName = "Items", Validations = [new ValidationRule { Type = ValidationType.MinItems, Value = 1L }, new ValidationRule { Type = ValidationType.NotEmpty }] },
            new() { FieldName = "Address", Validations = [new ValidationRule { Type = ValidationType.NotEmpty }] },
        ];
        var schema = new SchemaDefinitionExtended { SchemaName = "Lead", Fields = FieldDefinitionMapper.GetFieldDefinitionResponses(stored, [], validations) };

        var items = schema.Fields.Single(f => f.Name == "Items");
        Assert.Equal([ValidationType.MinItems], items.ValidationRule!.Validations.Select(r => r.Type));
        Assert.Null(schema.Fields.Single(f => f.Name == "Address").ValidationRule);

        Assert.False(new Dictionary<string, object?> { ["Items"] = new List<object?>() }.Validate(schema).IsValid);
        Assert.True(new Dictionary<string, object?> { ["Items"] = new List<object?> { new Dictionary<string, object?> { ["Qty"] = 1 } } }.Validate(schema).IsValid);
    }

    [Fact]
    public void NoDuplicates_OnALongList_IsFast_AndComparesAsEqualDoes()
    {
        var schema = (string type) => Schema("Tags", type, true, new ValidationRule { Type = ValidationType.UniqueItems });
        var many = Enumerable.Range(0, 20_000).Select(i => (object?)$"tag{i}").ToList();

        var clock = Stopwatch.StartNew();
        Assert.True(new Dictionary<string, object?> { ["Tags"] = many }.Validate(schema("String")).IsValid);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds} ms");

        many.Add("tag7");
        Assert.Contains("lists tag7 twice", Assert.Single(new Dictionary<string, object?> { ["Tags"] = many }.Validate(schema("String")).Errors).Message);
        Assert.True(new Dictionary<string, object?> { ["Tags"] = new List<object?> { "a", "A" } }.Validate(schema("String")).IsValid);
        Assert.False(new Dictionary<string, object?> { ["Tags"] = new List<object?> { 1L, 1.0 } }.Validate(schema("Float")).IsValid);
        Assert.False(new Dictionary<string, object?> { ["Tags"] = new List<object?> { 2, 2L } }.Validate(schema("Int")).IsValid);
    }

    [Fact]
    public void IsOneOf_WithoutAList_FailsClosed()
    {
        var schema = Schema("Country", "String", false, new ValidationRule { Type = ValidationType.In, Value = "DE" });
        var error = Assert.Single(new Dictionary<string, object?> { ["Country"] = "FR" }.Validate(schema).Errors);
        Assert.Equal(ValidationRuleCheck.ValuesMustBeAList, error.Message);
    }

    // ── Conditions and filters that can't be checked ──

    [Fact]
    public void AnEmptyNestedGroup_CantBeChecked_InAConditionOrAFilter()
    {
        var group = Where("Country", "DE");
        group.NestedGroups.Add(new PolicyRuleGroup());

        Assert.Equal(ConditionOutcome.CannotCheck, ValidationCondition.Evaluate(group, new BsonDocument("Country", "DE")));
        Assert.False(ReferenceLookup.TryCondition(new ValidationRule { Type = ValidationType.NotExistsIn, Value = "Blocklist", TargetFilter = group }, out _));
    }

    // ── API payloads ──

    [Fact]
    public void AbsentConditionsFiltersAndDescriptions_AreLeftOutOfJson()
    {
        var rule = System.Text.Json.JsonSerializer.Serialize(new ValidationRule { Type = ValidationType.NotEmpty });
        Assert.DoesNotContain("When", rule);
        Assert.DoesNotContain("TargetFilter", rule);
        Assert.DoesNotContain("Description", System.Text.Json.JsonSerializer.Serialize(new SchemaDefinition { SchemaName = "Lead" }));

        Assert.Contains("\"When\"", System.Text.Json.JsonSerializer.Serialize(new ValidationRule { Type = ValidationType.NotEmpty, When = Where("Country", "DE") }));
    }

    [Fact]
    public void ATargetRemembersWhetherItIsASystemEntity()
    {
        var rule = new ValidationRule { Type = ValidationType.NotExistsIn, Value = "User", SecondaryValue = "Email" };
        var lead = new SchemaDefinitionExtended
        {
            ItemId = "lead", SchemaName = "Lead", CollectionName = "Leads", SchemaType = SchemaType.Entity,
            Fields = [new FieldDefinitionResponse { Name = "Email", Type = "String", ValidationRule = new DataValidation { FieldName = "Email", Validations = [rule] } }],
        };
        var user = new SchemaDefinitionExtended
        {
            ItemId = "user", SchemaName = "User", CollectionName = "Users", SchemaType = SchemaType.Entity, IsSystemEntity = true,
            Fields = [new FieldDefinitionResponse { Name = "Email", Type = "String" }],
        };

        Assert.True(ReferenceTargets.Build([lead, user], strict: true, NullLogger.Instance).Find(rule)!.IsSystemEntity);
    }
}
