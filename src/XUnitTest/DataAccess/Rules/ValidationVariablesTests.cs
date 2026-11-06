using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>Validation rules that compare against another property of the same record (US-7).</summary>
public class ValidationVariablesTests
{
    private static SchemaDefinitionExtended Employee(ValidationRule rule) => new()
    {
        SchemaName = "Employee",
        Fields =
        [
            new FieldDefinitionResponse { Name = "StartDate", Type = "DateTime" },
            new FieldDefinitionResponse
            {
                Name = "EndDate", Type = "DateTime",
                ValidationRule = new DataValidation { FieldName = "EndDate", Validations = [rule] },
            },
        ],
    };

    private static ValidationRule After(string value, int version = 1, string? message = null) =>
        new() { Type = ValidationType.GreaterThan, Value = value, EvaluationVersion = version, ErrorMessage = message };

    private static readonly DateTime Jan10 = new(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EndDateBeforeStartDate_FailsWithAMessageNamingTheVariable()   // UC20
    {
        var input = new Dictionary<string, object?> { ["StartDate"] = Jan10, ["EndDate"] = Jan10.AddDays(-5) };

        var result = input.Validate(Employee(After("{{Employee.StartDate}}")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("EndDate", error.FieldName);
        Assert.Equal("EndDate must be greater than Employee.StartDate (2025-01-10)", error.Message);
        Assert.Equal(nameof(ValidationType.GreaterThan), error.ValidationType);
    }

    [Fact]
    public void Update_ReadsTheVariableFromTheStoredRecord()   // UC21
    {
        var input = new Dictionary<string, object?> { ["EndDate"] = Jan10.AddDays(20) };
        var stored = new Dictionary<string, object?> { ["StartDate"] = Jan10 };

        Assert.True(input.Validate(Employee(After("{{Employee.StartDate}}")), stored).IsValid);
        Assert.False(new Dictionary<string, object?> { ["EndDate"] = Jan10.AddDays(-1) }.Validate(Employee(After("{{Employee.StartDate}}")), stored).IsValid);
    }

    [Fact]
    public void VariableWithNoValue_SkipsTheRule()
    {
        var input = new Dictionary<string, object?> { ["EndDate"] = Jan10 };

        Assert.True(input.Validate(Employee(After("{{Employee.StartDate}}"))).IsValid);
    }

    [Fact]
    public void CustomMessage_StillWins()
    {
        var input = new Dictionary<string, object?> { ["StartDate"] = Jan10, ["EndDate"] = Jan10 };

        var result = input.Validate(Employee(After("{{Employee.StartDate}}", message: "Ends before it starts")));

        Assert.Equal("Ends before it starts", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void VersionZeroRule_KeepsItsValueAsText()
    {
        // Rules saved before variables existed are never read as variables, so a value that happens to look
        // like one keeps behaving exactly as before (here: an unparseable date bound, as today).
        var input = new Dictionary<string, object?> { ["StartDate"] = Jan10, ["EndDate"] = Jan10.AddDays(-5) };
        var legacy = new Dictionary<string, object?>(input).Validate(Employee(After("{{Employee.StartDate}}", version: 0)));
        var plainText = new Dictionary<string, object?>(input).Validate(Employee(After("not a date", version: 0)));

        Assert.Equal(plainText.IsValid, legacy.IsValid);
        Assert.DoesNotContain(legacy.Errors, e => e.Message.Contains("Employee.StartDate (2025-01-10)"));
    }

    [Fact]
    public void Range_WithAVariableBound()
    {
        var rule = new ValidationRule { Type = ValidationType.Range, Value = "{{Employee.StartDate}}", SecondaryValue = "2025-12-31", EvaluationVersion = 1 };
        var input = new Dictionary<string, object?> { ["StartDate"] = Jan10, ["EndDate"] = Jan10.AddDays(-1) };

        var error = Assert.Single(input.Validate(Employee(rule)).Errors);
        Assert.Equal("EndDate must be between Employee.StartDate (2025-01-10) and 2025-12-31", error.Message);
    }
}

/// <summary>Variables in validation values, checked when the rule is saved.</summary>
public class ValidationVariableCheckTests
{
    private static readonly IReadOnlyDictionary<string, VariableField> Fields = new Dictionary<string, VariableField>
    {
        ["StartDate"] = new("DateTime", false, false),
        ["Name"] = new("String", false, false),
        ["Tags"] = new("String", true, false),
    };

    private static string? Check(ValidationType type, object? value, string fieldType = "DateTime", int version = 1) =>
        ValidationVariableCheck.Check(new ValidationRule { Type = type, Value = value, EvaluationVersion = version }, fieldType, "Employee", Fields);

    [Fact]
    public void ValidVariable_InAComparison_IsAccepted()
    {
        Assert.Null(Check(ValidationType.GreaterThan, "{{Employee.StartDate}}"));
        Assert.Null(Check(ValidationType.Equal, "{{Employee.Name}}", "String"));
    }

    [Fact]
    public void Mismatches_AndMisuse_AreRefusedWithAReason()
    {
        Assert.Equal("Employee.Name is text at position 1: a date-time is expected", Check(ValidationType.GreaterThan, "{{Employee.Name}}"));
        Assert.Equal("Employee.Tags is a list at position 1: text is expected", Check(ValidationType.Equal, "{{Employee.Tags}}", "String"));
        Assert.Equal("Auth values aren't available in validation rules at position 1: use an access policy", Check(ValidationType.Equal, "{{Auth.UserId}}", "String"));
        Assert.Equal("A validation value is either a fixed value or one variable, e.g. {{Employee.Name}}", Check(ValidationType.Equal, "Hi {{Employee.Name}}", "String"));
        Assert.Equal("Missing prefix at position 1: write {{Employee.StartDate}}", Check(ValidationType.GreaterThan, "{{StartDate}}"));
        Assert.StartsWith("Only comparison rules", Check(ValidationType.MinLength, "{{Employee.StartDate}}"));
    }

    [Fact]
    public void FixedValues_AndVersionZeroRules_AreNotParsed()
    {
        Assert.Null(Check(ValidationType.Regex, "^[a-z]{2,3}$", "String"));       // braces in a fixed value
        Assert.Null(Check(ValidationType.GreaterThan, "{{Employee.Nope}}", version: 0));
    }
}
