using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// "Not empty" from evaluation version 3 also fails a property that an insert leaves out. Older rules, updates and the
/// bulk update input keep checking only what was sent (the G1 baseline).
/// </summary>
public class NotEmptyMissingFieldTests
{
    private static ValidationRule NotEmpty(int version, string? message = null, bool active = true) =>
        new() { Type = ValidationType.NotEmpty, EvaluationVersion = version, ErrorMessage = message, IsActive = active };

    private static FieldDefinitionResponse Field(string name, string type = "String", bool isArray = false, params ValidationRule[] rules) => new()
    {
        Name = name, Type = type, IsArray = isArray,
        ValidationRule = rules.Length == 0 ? null : new DataValidation { FieldName = name, Validations = [.. rules] },
    };

    private static SchemaDefinitionExtended Attendance(params FieldDefinitionResponse[] fields) => new() { SchemaName = "Attendance", Fields = [.. fields] };

    private static readonly Dictionary<string, object?> Stored = new() { ["EmployeeId"] = "e1", ["StartTime"] = "09:00" };

    [Fact]
    public void Version3_FailsAMissingProperty_OnInsert()
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3, "EmployeeId is required")), Field("StartTime"));

        var error = Assert.Single(new Dictionary<string, object?> { ["StartTime"] = "09:00" }.Validate(schema).Errors);

        Assert.Equal("EmployeeId", error.FieldName);
        Assert.Equal(nameof(ValidationType.NotEmpty), error.ValidationType);
        Assert.Equal("EmployeeId is required", error.Message);
    }

    [Fact]
    public void Version3_WithoutAMessage_UsesTheUsualNotEmptyMessage()
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3)));

        var error = Assert.Single(new Dictionary<string, object?>().Validate(schema).Errors);

        Assert.Equal("Field 'EmployeeId' cannot be empty.", error.Message);
    }

    [Fact]
    public void Version3_StillPassesASentValue_AndStillFailsABlankOne()
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3)));

        Assert.True(new Dictionary<string, object?> { ["EmployeeId"] = "e1" }.Validate(schema).IsValid);
        Assert.False(new Dictionary<string, object?> { ["EmployeeId"] = "  " }.Validate(schema).IsValid);
        Assert.False(new Dictionary<string, object?> { ["EmployeeId"] = null }.Validate(schema).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OlderVersions_KeepSkippingAMissingProperty(int version)
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(version)), Field("StartTime"));

        Assert.True(new Dictionary<string, object?> { ["StartTime"] = "09:00" }.Validate(schema).IsValid);
    }

    [Fact]
    public void Version3_OnUpdate_DoesNotFailAPropertyTheUpdateLeavesOut()
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3)), Field("StartTime"));

        Assert.True(new Dictionary<string, object?> { ["StartTime"] = "10:00" }.Validate(schema, Stored).IsValid);
    }

    [Fact]
    public void Version3_UpdateWithoutAStoredRecord_DoesNotFailAPropertyTheUpdateLeavesOut()
    {
        // Service data access (DataAccessResolver.ResolveWriteAsync) checks updates without loading the stored record.
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3)), Field("StartTime"));

        Assert.True(new Dictionary<string, object?> { ["StartTime"] = "10:00" }.Validate(schema, storedRecord: null, DateTime.UtcNow, isInsert: false).IsValid);
        Assert.False(new Dictionary<string, object?> { ["StartTime"] = "10:00" }.Validate(schema, storedRecord: null, DateTime.UtcNow, isInsert: true).IsValid);
    }

    [Fact]
    public void Version3_BulkUpdateInput_DoesNotFailAPropertyItLeavesOut()
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3)), Field("StartTime"));

        Assert.True(new Dictionary<string, object?> { ["StartTime"] = "10:00" }.ValidateWithoutConditions(schema, DateTime.UtcNow).IsValid);
    }

    [Fact]
    public void Version3_Inactive_IsSkipped()
    {
        var schema = Attendance(Field("EmployeeId", rules: NotEmpty(3, active: false)));

        Assert.True(new Dictionary<string, object?>().Validate(schema).IsValid);
    }

    [Fact]
    public void Version3_OnAListOfValues_FailsWhenTheListIsLeftOut()
    {
        var schema = Attendance(Field("Tags", isArray: true, rules: NotEmpty(3)));

        var error = Assert.Single(new Dictionary<string, object?>().Validate(schema).Errors);

        Assert.Equal("Tags", error.FieldName);
    }

    [Fact]
    public void Version3_InsideASentObject_FailsAMissingChild_ButAMissingObjectIsNotEntered()
    {
        var address = new FieldDefinitionResponse { Name = "Address", Type = "Address", Fields = [Field("City", rules: NotEmpty(3))] };
        var schema = Attendance(address);

        var error = Assert.Single(new Dictionary<string, object?> { ["Address"] = new Dictionary<string, object?> { ["Zip"] = "1000" } }.Validate(schema).Errors);
        Assert.Equal("Address.City", error.FieldName);

        Assert.True(new Dictionary<string, object?>().Validate(schema).IsValid);
    }

    [Fact]
    public void Version3_InsideAListOfObjects_FailsEachItemMissingTheChild()
    {
        var items = new FieldDefinitionResponse { Name = "Items", Type = "Item", IsArray = true, Fields = [Field("Sku", rules: NotEmpty(3))] };
        var schema = Attendance(items);
        var input = new Dictionary<string, object?>
        {
            ["Items"] = new List<object?> { new Dictionary<string, object?> { ["Sku"] = "a" }, new Dictionary<string, object?> { ["Qty"] = 2 } },
        };

        var error = Assert.Single(input.Validate(schema).Errors);

        Assert.Equal("Items[1].Sku", error.FieldName);
    }

    [Fact]
    public void Version3_WithAnOnlyWhenCondition_KeepsTheConditionalBehaviour()
    {
        var when = new PolicyRuleGroup
        {
            Rules = [new PolicyRule { LeftOperand = "Country", Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.STATIC_VALUE, StaticValue = "DE" }],
        };
        var rule = NotEmpty(3);
        rule.When = when;
        var schema = Attendance(Field("TaxId", rules: rule), Field("Country"));

        Assert.False(new Dictionary<string, object?> { ["Country"] = "DE" }.Validate(schema).IsValid);
        Assert.True(new Dictionary<string, object?> { ["Country"] = "AT" }.Validate(schema).IsValid);
    }
}
