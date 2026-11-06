using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// Rules with an "only when" condition (validation and access rules plan, section 1, phase 3). They run on the merged
/// record: on insert the input, on update the stored record with the input on top (an explicit null clears).
/// </summary>
public class ConditionalRulesTests
{
    private static PolicyRuleGroup When(string field, PolicyOperator op, object? value) => new()
    {
        Rules = [new() { LeftOperand = field, Operator = op, RightSource = ConditionSource.STATIC_VALUE, StaticValue = value }],
    };

    private static readonly PolicyRuleGroup CountryIsDe = When("Country", PolicyOperator.EQUAL, "DE");

    private static SchemaDefinitionExtended Customer(params ValidationRule[] vatRules) => new()
    {
        SchemaName = "Customer",
        Fields =
        [
            new FieldDefinitionResponse { Name = "Name", Type = "String" },
            new FieldDefinitionResponse { Name = "Country", Type = "String" },
            new FieldDefinitionResponse { Name = "BirthDate", Type = "DateTime" },
            new FieldDefinitionResponse
            {
                Name = "VatNumber", Type = "String",
                ValidationRule = new DataValidation { FieldName = "VatNumber", Validations = [.. vatRules] },
            },
        ],
    };

    private static ValidationRule Required(PolicyRuleGroup when) => new() { Type = ValidationType.NotEmpty, When = when, EvaluationVersion = 1 };

    private static DataValidationResult Insert(SchemaDefinitionExtended schema, Dictionary<string, object?> input) => input.Validate(schema);

    private static DataValidationResult Update(SchemaDefinitionExtended schema, Dictionary<string, object?> input, Dictionary<string, object?> stored) =>
        input.Validate(schema, stored);

    [Fact]
    public void RequiredWhen_ChecksAFieldThatWasNotSent()
    {
        var schema = Customer(Required(CountryIsDe));
        var error = Assert.Single(Insert(schema, new() { ["Country"] = "DE" }).Errors);
        Assert.Equal("VatNumber", error.FieldName);
        Assert.Equal(nameof(ValidationType.NotEmpty), error.ValidationType);

        Assert.True(Insert(schema, new() { ["Country"] = "AT" }).IsValid);
        Assert.True(Insert(schema, new() { ["Country"] = "DE", ["VatNumber"] = "DE123" }).IsValid);
    }

    [Fact]
    public void OtherRuleTypes_SkipAMissingValue_AndCheckASentOne()
    {
        var schema = Customer(new ValidationRule { Type = ValidationType.MinLength, Value = 5, When = CountryIsDe, EvaluationVersion = 1 });
        Assert.True(Insert(schema, new() { ["Country"] = "DE" }).IsValid);
        Assert.False(Insert(schema, new() { ["Country"] = "DE", ["VatNumber"] = "ab" }).IsValid);
        Assert.True(Insert(schema, new() { ["Country"] = "AT", ["VatNumber"] = "ab" }).IsValid);
    }

    [Fact]
    public void Update_ReadsTheConditionFromTheStoredRecord()
    {
        var schema = Customer(Required(CountryIsDe));
        Assert.False(Update(schema, new() { ["VatNumber"] = "" }, new() { ["Country"] = "DE", ["VatNumber"] = "DE123" }).IsValid);
        Assert.True(Update(schema, new() { ["VatNumber"] = "" }, new() { ["Country"] = "AT" }).IsValid);
    }

    [Fact]
    public void Update_AnExplicitNullClears_TheFieldAndTheCondition()
    {
        var schema = Customer(Required(CountryIsDe));
        // Clearing the VAT number of a German customer is refused.
        Assert.False(Update(schema, new() { ["VatNumber"] = null }, new() { ["Country"] = "DE", ["VatNumber"] = "DE123" }).IsValid);
        // Clearing the country as well: the condition no longer holds.
        Assert.True(Update(schema, new() { ["Country"] = null, ["VatNumber"] = null }, new() { ["Country"] = "DE", ["VatNumber"] = "DE123" }).IsValid);
    }

    [Fact]
    public void Update_TouchingNeitherTheFieldNorTheCondition_DoesNotFailOnOlderData()
    {
        var schema = Customer(Required(CountryIsDe));
        // A German customer saved before the rule existed, with no VAT number: renaming them still works.
        Assert.True(Update(schema, new() { ["Name"] = "New name" }, new() { ["Country"] = "DE" }).IsValid);
        // Changing the country (a field the condition uses) checks the rule.
        Assert.False(Update(schema, new() { ["Country"] = "DE" }, new() { ["Country"] = "AT" }).IsValid);
    }

    [Fact]
    public void TheRecord_IsTypedFromTheSchema_SoADateSentAsTextComparesAsADate()
    {
        var schema = Customer(Required(When("BirthDate", PolicyOperator.GREATER_THAN, "2000-01-01")));
        Assert.False(Insert(schema, new() { ["BirthDate"] = "2001-05-01" }).IsValid);
        Assert.True(Insert(schema, new() { ["BirthDate"] = "1999-05-01" }).IsValid);
    }

    [Fact]
    public void TheRawInput_KeepsExplicitNulls_ThatAToRecordInputLoses()
    {
        // ResolveWriteAsync validates a ToRecord() copy of the input, which drops nulls; it also passes the raw document,
        // so clearing a field still counts as sending it, and as clearing it.
        var schema = Customer(Required(CountryIsDe));
        var stored = new Dictionary<string, object?> { ["Country"] = "DE", ["VatNumber"] = "DE123" };
        var raw = new BsonDocument("VatNumber", BsonNull.Value);

        Assert.False(new Dictionary<string, object?>().Validate(schema, stored, DateTime.UtcNow, rawInput: raw).IsValid);
        Assert.True(new Dictionary<string, object?>().Validate(schema, stored).IsValid);   // without it, nothing was sent
    }

    [Fact]
    public void AConditionThatCannotBeChecked_FailsTheRuleWithAClearMessage()
    {
        var schema = Customer(Required(When("Name", PolicyOperator.REGEX, "a(b")));
        var error = Assert.Single(Insert(schema, new() { ["Name"] = "x", ["VatNumber"] = "DE1" }).Errors);
        Assert.Equal("VatNumber", error.FieldName);
        Assert.Equal(DataValidationHelper.ConditionNotChecked, error.Message);
    }

    [Fact]
    public void ACustomMessage_Wins()
    {
        var rule = Required(CountryIsDe);
        rule.ErrorMessage = "German customers need a VAT number";
        Assert.Equal("German customers need a VAT number", Assert.Single(Insert(Customer(rule), new() { ["Country"] = "DE" }).Errors).Message);
    }

    [Fact]
    public void BulkUpdate_ChecksTheInputOnce_ThenEachRecordsConditionalRules()
    {
        // Bulk update validates the input once without a stored record, as today, then the conditional rules once per
        // matched record. Splitting Validate this way gives the same errors as Validate on one record.
        var schema = Customer(Required(CountryIsDe), new ValidationRule { Type = ValidationType.MaxLength, Value = 3, EvaluationVersion = 1 });
        var input = new Dictionary<string, object?> { ["VatNumber"] = "" };
        var german = new Dictionary<string, object?> { ["Country"] = "DE", ["VatNumber"] = "DE1" };
        var austrian = new Dictionary<string, object?> { ["Country"] = "AT" };
        var now = DateTime.UtcNow;

        Assert.True(input.ValidateWithoutConditions(schema, now).IsValid);
        Assert.Equal(nameof(ValidationType.NotEmpty), Assert.Single(input.ValidateConditions(schema, german, now).Errors).ValidationType);
        Assert.True(input.ValidateConditions(schema, austrian, now).IsValid);

        var tooLong = new Dictionary<string, object?> { ["VatNumber"] = "DE12345" };
        Assert.Equal(nameof(ValidationType.MaxLength), Assert.Single(tooLong.ValidateWithoutConditions(schema, now).Errors).ValidationType);
        Assert.True(tooLong.ValidateConditions(schema, german, now).IsValid);   // only conditional rules here
    }

    [Fact]
    public void ANestedProperty_CanBeRequiredWhen()
    {
        var schema = new SchemaDefinitionExtended
        {
            SchemaName = "Customer",
            Fields =
            [
                new FieldDefinitionResponse { Name = "Country", Type = "String" },
                new FieldDefinitionResponse
                {
                    Name = "Address", Type = "Address",
                    Fields =
                    [
                        new FieldDefinitionResponse
                        {
                            Name = "City", Type = "String",
                            ValidationRule = new DataValidation { FieldName = "Address.City", Validations = [Required(CountryIsDe)] },
                        },
                    ],
                },
            ],
        };
        var error = Assert.Single(Insert(schema, new() { ["Country"] = "DE", ["Address"] = new Dictionary<string, object?>() }).Errors);
        Assert.Equal("Address.City", error.FieldName);
        Assert.True(Insert(schema, new() { ["Country"] = "DE", ["Address"] = new Dictionary<string, object?> { ["City"] = "Berlin" } }).IsValid);
    }

    [Fact]
    public void AListRule_CanHaveACondition()
    {
        var schema = new SchemaDefinitionExtended
        {
            SchemaName = "Customer",
            Fields =
            [
                new FieldDefinitionResponse { Name = "Country", Type = "String" },
                new FieldDefinitionResponse
                {
                    Name = "Tags", Type = "String", IsArray = true,
                    ValidationRule = new DataValidation { FieldName = "Tags", Validations = [new() { Type = ValidationType.MinItems, Value = 1L, When = CountryIsDe }] },
                },
            ],
        };
        Assert.False(Insert(schema, new() { ["Country"] = "DE", ["Tags"] = new List<object?>() }).IsValid);
        Assert.True(Insert(schema, new() { ["Country"] = "AT", ["Tags"] = new List<object?>() }).IsValid);
    }
}
