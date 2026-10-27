using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// Evaluating "is one of", the list rules and dates relative to now (validation and access rules plan, phase 2).
/// The new types never read EvaluationVersion (R2): each behaves one fixed way.
/// </summary>
public class NewRuleTypesTests
{
    private static SchemaDefinitionExtended Customer(string field, string type, bool isArray, params ValidationRule[] rules) => new()
    {
        SchemaName = "Customer",
        Fields =
        [
            new FieldDefinitionResponse
            {
                Name = field, Type = type, IsArray = isArray,
                ValidationRule = new DataValidation { FieldName = field, Validations = [.. rules] },
            },
        ],
    };

    private static DataValidationResult Validate(SchemaDefinitionExtended schema, string field, object? value) =>
        new Dictionary<string, object?> { [field] = value }.Validate(schema);

    private static ValidationRule Rule(ValidationType type, object? value = null, int version = 0) =>
        new() { Type = type, Value = value, EvaluationVersion = version };

    // In / NotIn
    [Fact]
    public void In_PassesForAListedValue_AndMatchesExactly()
    {
        var schema = Customer("Country", "String", false, Rule(ValidationType.In, new object[] { "DE", "AT" }));
        Assert.True(Validate(schema, "Country", "DE").IsValid);
        Assert.False(Validate(schema, "Country", "FR").IsValid);
        Assert.False(Validate(schema, "Country", "de").IsValid);   // exact, as Equal (decision 3)
    }

    [Fact]
    public void In_SaysWhichValuesAreAllowed()
    {
        var schema = Customer("Country", "String", false, Rule(ValidationType.In, new object[] { "DE", "AT" }));
        var error = Assert.Single(Validate(schema, "Country", "FR").Errors);
        Assert.Equal("Country", error.FieldName);
        Assert.Equal("Field 'Country' must be one of: DE, AT.", error.Message);
        Assert.Equal(nameof(ValidationType.In), error.ValidationType);
    }

    [Fact]
    public void NotIn_FailsForAListedValue()
    {
        var schema = Customer("Country", "String", false, Rule(ValidationType.NotIn, new List<object> { "DE" }));   // as read back from Mongo
        Assert.True(Validate(schema, "Country", "FR").IsValid);
        var error = Assert.Single(Validate(schema, "Country", "DE").Errors);
        Assert.Equal("Field 'Country' must not be one of: DE.", error.Message);
    }

    [Fact]
    public void In_ComparesNumbersAsNumbers()
    {
        Assert.True(Validate(Customer("Age", "Int", false, Rule(ValidationType.In, new object[] { 1L, 2L })), "Age", 2).IsValid);
        Assert.True(Validate(Customer("Score", "Float", false, Rule(ValidationType.In, new object[] { 1.5, 2L })), "Score", 2.0).IsValid);
        Assert.False(Validate(Customer("Age", "Int", false, Rule(ValidationType.In, new object[] { 1L, 2L })), "Age", 3).IsValid);
    }

    [Fact]
    public void In_OnAListProperty_ChecksEachItem_WhateverTheVersion()
    {
        var schema = Customer("Tags", "String", true, Rule(ValidationType.In, new object[] { "vip", "new" }, version: 0));
        Assert.True(Validate(schema, "Tags", new List<object?> { "vip", "new" }).IsValid);
        var error = Assert.Single(Validate(schema, "Tags", new List<object?> { "vip", "old" }).Errors);
        Assert.Equal("Tags[1]", error.FieldName);
    }

    [Fact]
    public void In_SkipsAMissingOrNullValue_LikeEveryOtherRule()
    {
        var schema = Customer("Country", "String", false, Rule(ValidationType.In, new object[] { "DE" }));
        Assert.True(Validate(schema, "Country", null).IsValid);
        Assert.True(new Dictionary<string, object?>().Validate(schema).IsValid);
    }

    // MinItems, MaxItems, UniqueItems: always the whole list (section 4).
    [Fact]
    public void MinItems_And_MaxItems_CountTheWholeList_WhateverTheVersion()
    {
        var schema = Customer("Tags", "String", true, Rule(ValidationType.MinItems, 2L), Rule(ValidationType.MaxItems, 3L, version: 1));
        Assert.True(Validate(schema, "Tags", new List<object?> { "a", "b" }).IsValid);

        var tooFew = Assert.Single(Validate(schema, "Tags", new List<object?> { "a" }).Errors);
        Assert.Equal("Tags", tooFew.FieldName);
        Assert.Equal("Field 'Tags' must have at least 2 items.", tooFew.Message);
        Assert.Equal(nameof(ValidationType.MinItems), tooFew.ValidationType);

        var tooMany = Assert.Single(Validate(schema, "Tags", new List<object?> { "a", "b", "c", "d" }).Errors);
        Assert.Equal("Field 'Tags' must have at most 3 items.", tooMany.Message);
    }

    [Fact]
    public void ListRules_SkipAMissingList()
    {
        // To require at least one item, "Required" already refuses an empty list (section 4).
        var schema = Customer("Tags", "String", true, Rule(ValidationType.MinItems, 1L));
        Assert.True(Validate(schema, "Tags", null).IsValid);
    }

    [Fact]
    public void UniqueItems_NamesTheRepeatedValue_AndMatchesExactly()
    {
        var schema = Customer("Tags", "String", true, Rule(ValidationType.UniqueItems));
        Assert.True(Validate(schema, "Tags", new List<object?> { "a", "A" }).IsValid);
        var error = Assert.Single(Validate(schema, "Tags", new List<object?> { "a", "b", "a" }).Errors);
        Assert.Equal("Tags", error.FieldName);
        Assert.Equal("Field 'Tags' lists a twice.", error.Message);
        Assert.Equal(nameof(ValidationType.UniqueItems), error.ValidationType);
    }

    [Fact]
    public void UniqueItems_ComparesNumbersAsNumbers()
    {
        var schema = Customer("Scores", "Float", true, Rule(ValidationType.UniqueItems));
        Assert.False(Validate(schema, "Scores", new List<object?> { 2L, 2.0 }).IsValid);
    }

    [Fact]
    public void NotExistsIn_IsLeftToTheReferenceStep()
    {
        // It needs the target schema's data, like Reference (phase 4).
        var schema = Customer("Email", "String", false, Rule(ValidationType.NotExistsIn, "BlockedCustomer"));
        Assert.True(Validate(schema, "Email", "a@b.c").IsValid);
    }

    [Fact]
    public void ACustomMessage_Wins_ForTheNewTypes()
    {
        var rule = new ValidationRule { Type = ValidationType.MinItems, Value = 1L, ErrorMessage = "Add a tag" };
        var error = Assert.Single(Validate(Customer("Tags", "String", true, rule), "Tags", new List<object?>()).Errors);
        Assert.Equal("Add a tag", error.Message);
    }

    // Dates relative to now (section 3): UTC, calendar maths, the bound named in the message.
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static DataValidationResult ValidateAt(SchemaDefinitionExtended schema, object? birthDate) =>
        new Dictionary<string, object?> { ["BirthDate"] = birthDate }.Validate(schema, storedRecord: null, nowUtc: Now);

    [Fact]
    public void Now_EighteenYearsAgo_UsesCalendarYears()
    {
        var schema = Customer("BirthDate", "DateTime", false, Rule(ValidationType.LessThanOrEqual, "{{Now-18y}}", version: 1));
        Assert.True(ValidateAt(schema, new DateTime(2008, 10, 25, 0, 0, 0, DateTimeKind.Utc)).IsValid);
        var error = Assert.Single(ValidateAt(schema, new DateTime(2008, 10, 26, 0, 0, 0, DateTimeKind.Utc)).Errors);
        Assert.Equal("BirthDate must be on or before now − 18 years (2008-10-25)", error.Message);
    }

    [Fact]
    public void Now_InEveryOrdering_AndARange()
    {
        var after = Customer("BirthDate", "DateTime", false, Rule(ValidationType.GreaterThan, "{{Now}}", version: 1));
        Assert.True(ValidateAt(after, Now.AddMinutes(1)).IsValid);
        Assert.Equal("BirthDate must be after now (2026-10-25 12:00 UTC)", Assert.Single(ValidateAt(after, Now.AddMinutes(-1)).Errors).Message);

        var range = new ValidationRule { Type = ValidationType.Range, Value = "{{Now-1mo}}", SecondaryValue = "{{Now+30d}}", EvaluationVersion = 1 };
        var schema = Customer("BirthDate", "DateTime", false, range);
        Assert.True(ValidateAt(schema, Now).IsValid);
        Assert.Equal(
            "BirthDate must be between now − 1 month (2026-09-25) and now + 30 days (2026-11-24)",
            Assert.Single(ValidateAt(schema, Now.AddMonths(-2)).Errors).Message);

        var hours = Customer("BirthDate", "DateTime", false, Rule(ValidationType.GreaterThanOrEqual, "{{Now-2h}}", version: 1));
        Assert.Equal("BirthDate must be on or after now − 2 hours (2026-10-25 10:00 UTC)", Assert.Single(ValidateAt(hours, Now.AddHours(-3)).Errors).Message);
    }

    [Fact]
    public void Now_CustomMessageWins()
    {
        var rule = new ValidationRule { Type = ValidationType.LessThan, Value = "{{Now}}", EvaluationVersion = 1, ErrorMessage = "Must be in the past" };
        Assert.Equal("Must be in the past", Assert.Single(ValidateAt(Customer("BirthDate", "DateTime", false, rule), Now.AddDays(1)).Errors).Message);
    }

    [Fact]
    public void Now_WithoutAClock_UsesTheCurrentTime()
    {
        var schema = Customer("BirthDate", "DateTime", false, Rule(ValidationType.LessThan, "{{Now}}", version: 1));
        Assert.True(new Dictionary<string, object?> { ["BirthDate"] = DateTime.UtcNow.AddDays(-1) }.Validate(schema).IsValid);
        Assert.False(new Dictionary<string, object?> { ["BirthDate"] = DateTime.UtcNow.AddDays(1) }.Validate(schema).IsValid);
    }
}
