using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>The save-time check for the new validation features (validation and access rules plan, phase 1).</summary>
public class ValidationRuleCheckTests
{
    private static readonly IReadOnlyDictionary<string, VariableField> Fields = new Dictionary<string, VariableField>
    {
        ["Name"] = new("String", false, false),
        ["Country"] = new("String", false, false),
        ["Age"] = new("Int", false, false),
        ["Score"] = new("Float", false, false),
        ["Status"] = new("Enum", false, false),
        ["BirthDate"] = new("DateTime", false, false),
        ["Active"] = new("Boolean", false, false),
        ["Tags"] = new("String", true, false),
        ["Items"] = new("Item", true, true),
        ["Items.Qty"] = new("Int", false, false),
    };

    private static RuleField Field(string path, IReadOnlyList<string>? enumValues = null) =>
        new(path, Fields[path].Type, Fields[path].IsArray, enumValues);

    private static IReadOnlyList<string> Check(ValidationRule rule, string path = "Name", bool system = false, IReadOnlyList<string>? enumValues = null) =>
        ValidationRuleCheck.Check(rule, Field(path, enumValues), "Customer", system, Fields);

    // G12: every rule that exists today passes untouched, whatever its version and values.
    public static TheoryData<ValidationType, object?, object?, int> OldRules() => new()
    {
        { ValidationType.NotEmpty, "^[^@]+@[^@]+$", null, 0 },   // as all 15 Almora rules: NotEmpty with a Value
        { ValidationType.NotEmpty, null, null, 1 },
        { ValidationType.Regex, "^[a-z]{2,3}$", null, 0 },
        { ValidationType.MinLength, 2, null, 1 },
        { ValidationType.MaxLength, 50, null, 0 },
        { ValidationType.LengthRange, 2, 50, 1 },
        { ValidationType.Equal, "DE", null, 0 },
        { ValidationType.NotEqual, "{{Customer.Country}}", null, 1 },
        { ValidationType.GreaterThan, 18, null, 1 },
        { ValidationType.LessThan, 99, null, 0 },
        { ValidationType.GreaterThanOrEqual, 0, null, 1 },
        { ValidationType.LessThanOrEqual, 100, null, 1 },
        { ValidationType.Range, 1, 9, 0 },
        { ValidationType.Reference, "Customer", "ItemId", 1 },
    };

    [Theory]
    [MemberData(nameof(OldRules))]
    public void ARuleOfAnExistingType_WithoutTheNewProperties_IsNeverRefused(ValidationType type, object? value, object? secondary, int version)
    {
        var rule = new ValidationRule { Type = type, Value = value, SecondaryValue = secondary, EvaluationVersion = version };
        Assert.Empty(Check(rule));
        Assert.Empty(Check(rule, system: true));
    }

    public static TheoryData<ValidationType> NewTypes() => new()
    {
        ValidationType.In, ValidationType.NotIn, ValidationType.MinItems, ValidationType.MaxItems, ValidationType.UniqueItems, ValidationType.NotExistsIn,
    };

    [Theory]
    [MemberData(nameof(NewTypes))]
    public void ANewType_OnASystemSchema_IsRefused(ValidationType type)
    {
        var problems = Check(new ValidationRule { Type = type, Value = new object[] { "a" } }, path: "Tags", system: true);
        Assert.Contains(ValidationRuleCheck.NotOnSystemSchemas, problems);
    }

    [Fact]
    public void AConditionOrAReferenceFilter_OnASystemSchema_IsRefused()
    {
        var group = new PolicyRuleGroup { Rules = [new() { LeftOperand = "Country", StaticValue = "DE" }] };
        Assert.Contains(ValidationRuleCheck.NotOnSystemSchemas, Check(new ValidationRule { Type = ValidationType.NotEmpty, When = group }, system: true));
        Assert.Contains(ValidationRuleCheck.NotOnSystemSchemas, Check(new ValidationRule { Type = ValidationType.Reference, Value = "Customer", TargetFilter = group }, system: true));
    }

    [Fact]
    public void ADateRelativeToNow_OnASystemSchema_IsRefused()
    {
        // A platform service on an older Genesis would compare "{{Now}}" as a date and reject every write.
        var rule = new ValidationRule { Type = ValidationType.GreaterThan, Value = "{{Now}}", EvaluationVersion = 1 };
        Assert.Equal([ValidationRuleCheck.NotOnSystemSchemas], Check(rule, "BirthDate", system: true));
    }

    // In / NotIn: a list of values of the property's type (section 2).
    private static ValidationRule In(params object[] values) => new() { Type = ValidationType.In, Value = values };

    [Fact]
    public void In_WithValuesOfThePropertysType_IsAccepted()
    {
        Assert.Empty(Check(In("DE", "AT"), "Country"));
        Assert.Empty(Check(In(1L, 2L), "Age"));
        Assert.Empty(Check(In(1.5, 2L), "Score"));
        Assert.Empty(Check(In("Open", "Closed"), "Status", enumValues: ["Open", "Closed", "Archived"]));
        Assert.Empty(Check(In("vip", "new"), "Tags"));   // a list property: each item is checked
        Assert.Empty(Check(new ValidationRule { Type = ValidationType.NotIn, Value = new List<object> { "DE" } }, "Country"));
    }

    [Theory]
    [InlineData("BirthDate", "date-time")]
    [InlineData("Active", "true/false")]
    public void In_OnADateOrBooleanProperty_IsRefused(string path, string kind)
    {
        Assert.Equal([ValidationRuleCheck.ValuesNotForType(kind)], Check(In("x"), path));
    }

    [Fact]
    public void In_NeedsANonEmptyListOfAtMost500Values()
    {
        Assert.Equal([ValidationRuleCheck.ValuesMustBeAList], Check(new ValidationRule { Type = ValidationType.In, Value = "DE,AT" }, "Country"));
        Assert.Equal([ValidationRuleCheck.ValuesMustBeAList], Check(new ValidationRule { Type = ValidationType.In }, "Country"));
        Assert.Equal([ValidationRuleCheck.ValuesMustNotBeEmpty], Check(In(), "Country"));
        Assert.Equal([ValidationRuleCheck.TooManyValues], Check(In(Enumerable.Range(0, 501).Select(i => (object)$"v{i}").ToArray()), "Country"));
        Assert.Empty(Check(In(Enumerable.Range(0, 500).Select(i => (object)$"v{i}").ToArray()), "Country"));
    }

    [Fact]
    public void In_AValueThatIsNotOfThePropertysType_IsRefused()
    {
        Assert.Equal([ValidationRuleCheck.ValueDoesNotFit("x", "whole number")], Check(In(1L, "x"), "Age"));
        Assert.Equal([ValidationRuleCheck.ValueDoesNotFit("1.5", "whole number")], Check(In(1.5), "Age"));
        Assert.Equal([ValidationRuleCheck.ValueDoesNotFit("x", "number")], Check(In("x"), "Score"));
        Assert.Equal([ValidationRuleCheck.ValueDoesNotFit("Gone", "value of Status")], Check(In("Open", "Gone"), "Status", enumValues: ["Open", "Closed"]));
        Assert.Equal([ValidationRuleCheck.ValueDoesNotFit("5", "text")], Check(In(5L), "Country"));
    }

    [Fact]
    public void In_AValueListedTwice_IsRefused()
    {
        Assert.Equal([ValidationRuleCheck.DuplicateValue("DE")], Check(In("DE", "AT", "DE"), "Country"));
        Assert.Equal([ValidationRuleCheck.DuplicateValue("2")], Check(In(2L, 2.0), "Score"));
        Assert.Empty(Check(In("de", "DE"), "Country"));   // exact match, as Equal: these are two values
    }

    // MinItems, MaxItems, UniqueItems: list properties only (section 4).
    [Theory]
    [InlineData(ValidationType.MinItems)]
    [InlineData(ValidationType.MaxItems)]
    public void ItemCount_NeedsAListPropertyAndAWholeNumber(ValidationType type)
    {
        Assert.Empty(Check(new ValidationRule { Type = type, Value = 2L }, "Tags"));
        Assert.Empty(Check(new ValidationRule { Type = type, Value = 0 }, "Tags"));
        Assert.Equal([ValidationRuleCheck.OnlyForLists], Check(new ValidationRule { Type = type, Value = 2L }, "Name"));
        Assert.Equal([ValidationRuleCheck.CountMustBeWholeNumber], Check(new ValidationRule { Type = type, Value = -1L }, "Tags"));
        Assert.Equal([ValidationRuleCheck.CountMustBeWholeNumber], Check(new ValidationRule { Type = type, Value = 1.5 }, "Tags"));
        Assert.Equal([ValidationRuleCheck.CountMustBeWholeNumber], Check(new ValidationRule { Type = type }, "Tags"));
    }

    [Fact]
    public void UniqueItems_NeedsAListProperty()
    {
        Assert.Empty(Check(new ValidationRule { Type = ValidationType.UniqueItems }, "Tags"));
        Assert.Equal([ValidationRuleCheck.OnlyForLists], Check(new ValidationRule { Type = ValidationType.UniqueItems }, "Name"));
    }

    // NotExistsIn: never against its own schema (section 5).
    [Fact]
    public void NotExistsIn_AgainstItsOwnSchema_IsRefused_PointingAtTheUniqueFlag()
    {
        Assert.Empty(Check(new ValidationRule { Type = ValidationType.NotExistsIn, Value = "BlockedCustomer", SecondaryValue = "Name" }));
        Assert.Equal([ValidationRuleCheck.NotExistsInOwnSchema], Check(new ValidationRule { Type = ValidationType.NotExistsIn, Value = "customer" }));
        Assert.Equal([ValidationRuleCheck.TargetSchemaRequired], Check(new ValidationRule { Type = ValidationType.NotExistsIn, Value = " " }));
    }

    // "Only when" conditions (section 1).
    private static PolicyRule Fixed(string left, PolicyOperator op, object? value) =>
        new() { LeftSource = ConditionSource.SCHEMA_FIELD, LeftOperand = left, Operator = op, RightSource = ConditionSource.STATIC_VALUE, StaticValue = value };

    private static PolicyRule Compare(string left, PolicyOperator op, string right) =>
        new() { LeftSource = ConditionSource.SCHEMA_FIELD, LeftOperand = left, Operator = op, RightSource = ConditionSource.SCHEMA_FIELD, RightOperand = right };

    private static ValidationRule When(params PolicyRule[] rules) =>
        new() { Type = ValidationType.NotEmpty, When = new PolicyRuleGroup { Rules = [.. rules] } };

    [Fact]
    public void Condition_OnFixedValuesAndOtherProperties_IsAccepted()
    {
        Assert.Empty(Check(When(Fixed("Country", PolicyOperator.EQUAL, "DE"))));
        Assert.Empty(Check(When(Fixed("Age", PolicyOperator.GREATER_THAN_OR_EQUAL, "18"), Fixed("Status", PolicyOperator.IN, "Open,Closed"))));
        Assert.Empty(Check(When(Fixed("Country", PolicyOperator.IS_NOT_NULL, null))));
        Assert.Empty(Check(When(Fixed("Name", PolicyOperator.REGEX, "^[A-Z]"))));
        Assert.Empty(Check(When(Compare("Age", PolicyOperator.LESS_THAN, "Score"))));
        Assert.Empty(Check(When(Compare("Name", PolicyOperator.EQUAL, "Country"))));
        var nested = new ValidationRule
        {
            Type = ValidationType.NotEmpty,
            When = new PolicyRuleGroup { LogicalOperator = PolicyLogicalOperator.OR, NestedGroups = [new() { Rules = [Fixed("Active", PolicyOperator.EQUAL, "true")] }] },
        };
        Assert.Empty(Check(nested));
    }

    [Fact]
    public void Condition_LeftSideMustBeAPropertyOfTheRecord()
    {
        // A fixed value or an Auth value on the left reaches the builder's catch-all, which grants full access.
        var fixedLeft = new PolicyRule { LeftSource = ConditionSource.STATIC_VALUE, LeftOperand = "x", Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.STATIC_VALUE, StaticValue = "x" };
        var authLeft = new PolicyRule { LeftSource = ConditionSource.AUTH, LeftOperand = "UserId", Operator = PolicyOperator.EQUAL, StaticValue = "u1" };
        Assert.Equal([ValidationRuleCheck.ConditionLeftMustBeProperty], Check(When(fixedLeft)));
        Assert.Equal([ValidationRuleCheck.ConditionLeftMustBeProperty], Check(When(authLeft)));
        Assert.Equal([ValidationRuleCheck.UnknownProperty("Nope")], Check(When(Fixed("Nope", PolicyOperator.EQUAL, "x"))));
    }

    [Fact]
    public void Condition_AuthValues_AreRefused_EvenInsideAFixedValue()
    {
        var authRight = new PolicyRule { LeftOperand = "Name", Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.AUTH, RightOperand = "UserId" };
        Assert.Equal([ValidationRuleCheck.AuthNotAllowed], Check(When(authRight)));
        Assert.Equal([ValidationRuleCheck.PlaceholderNotAllowed], Check(When(Fixed("Name", PolicyOperator.EQUAL, "{{Auth.UserId}}"))));
        Assert.Equal([ValidationRuleCheck.PlaceholderNotAllowed], Check(When(Fixed("Name", PolicyOperator.EQUAL, "{{token.email}}"))));
        Assert.Equal([ValidationRuleCheck.PlaceholderNotAllowed], Check(When(Fixed("Name", PolicyOperator.EQUAL, "{{Customer.Country}}"))));
    }

    [Theory]
    [InlineData(PolicyOperator.START_WITH)]
    [InlineData(PolicyOperator.END_WITH)]
    [InlineData(PolicyOperator.REGEX)]
    [InlineData(PolicyOperator.IS_NULL)]
    [InlineData(PolicyOperator.CONTAIN)]
    public void Condition_ComparingTwoProperties_OnlyWithEqualityAndOrdering(PolicyOperator op)
    {
        // The field-to-field builder turns every other operator into $eq.
        Assert.Equal([ValidationRuleCheck.FieldComparisonOperator], Check(When(Compare("Name", op, "Country"))));
    }

    [Fact]
    public void Condition_ComparingTwoProperties_NeverOnItemId_AndOnlyOfTheSameKind()
    {
        Assert.Equal([ValidationRuleCheck.FieldComparisonItemId], Check(When(Compare("ItemId", PolicyOperator.EQUAL, "Name"))));
        Assert.Equal([ValidationRuleCheck.FieldComparisonItemId], Check(When(Compare("Name", PolicyOperator.EQUAL, "ItemId"))));
        Assert.Equal([ValidationRuleCheck.FieldComparisonKind("Age", "BirthDate")], Check(When(Compare("Age", PolicyOperator.LESS_THAN, "BirthDate"))));
        Assert.Equal([ValidationRuleCheck.UnknownProperty("Nope")], Check(When(Compare("Name", PolicyOperator.EQUAL, "Nope"))));
    }

    [Fact]
    public void Condition_ACommaInASingleValue_IsRefused_ButIsOneOfTakesALIst()
    {
        // The builder would split "Smith, John" into a list and match either part.
        Assert.Equal([ValidationRuleCheck.CommaInSingleValue], Check(When(Fixed("Name", PolicyOperator.EQUAL, "Smith, John"))));
        Assert.Empty(Check(When(Fixed("Name", PolicyOperator.IN, "Smith,John"))));
    }

    [Fact]
    public void Condition_ARegexThatIsNotAPattern_OrAMissingValue_IsRefused()
    {
        Assert.Equal([ValidationRuleCheck.InvalidPattern("a(b")], Check(When(Fixed("Name", PolicyOperator.REGEX, "a(b"))));
        Assert.Equal([ValidationRuleCheck.ConditionNeedsValue], Check(When(Fixed("Name", PolicyOperator.EQUAL, null))));
        Assert.Equal([ValidationRuleCheck.ConditionNeedsValue], Check(When(Fixed("Name", PolicyOperator.EQUAL, " "))));
    }

    [Fact]
    public void Condition_OnAPropertyInsideAListOfObjects_IsRefused()
    {
        // "Same item" scoping needs its own design (section 1, limits).
        Assert.Equal([ValidationRuleCheck.ConditionInsideList], Check(When(Fixed("Country", PolicyOperator.EQUAL, "DE")), "Items.Qty"));
    }

    // The reference filter (section 5): Reference and NotExistsIn only; target properties against fixed values.
    private static ValidationRule Filtered(ValidationType type, params PolicyRule[] rules) =>
        new() { Type = type, Value = "BlockedCustomer", TargetFilter = new PolicyRuleGroup { Rules = [.. rules] } };

    [Fact]
    public void Filter_OnAReferenceOrNotExistsIn_ComparingTargetPropertiesWithFixedValues_IsAccepted()
    {
        // Left operands are the target's properties, checked against the target schema by the caller.
        Assert.Empty(Check(Filtered(ValidationType.Reference, Fixed("Status", PolicyOperator.EQUAL, "Active"))));
        Assert.Empty(Check(Filtered(ValidationType.NotExistsIn, Fixed("Reason", PolicyOperator.IS_NOT_NULL, null))));
    }

    [Fact]
    public void Filter_OnAnyOtherRuleType_IsRefused()
    {
        Assert.Equal([ValidationRuleCheck.FilterOnlyForReferences], Check(Filtered(ValidationType.NotEmpty, Fixed("Status", PolicyOperator.EQUAL, "Active"))));
    }

    [Fact]
    public void Filter_ComparesWithFixedValuesOnly()
    {
        var withField = new PolicyRule { LeftOperand = "Status", Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.SCHEMA_FIELD, RightOperand = "Country" };
        var withAuth = new PolicyRule { LeftOperand = "Owner", Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.AUTH, RightOperand = "UserId" };
        Assert.Equal([ValidationRuleCheck.FilterMustUseFixedValues], Check(Filtered(ValidationType.Reference, withField)));
        Assert.Equal([ValidationRuleCheck.FilterMustUseFixedValues], Check(Filtered(ValidationType.Reference, withAuth)));
        Assert.Equal([ValidationRuleCheck.PlaceholderNotAllowed], Check(Filtered(ValidationType.Reference, Fixed("Owner", PolicyOperator.EQUAL, "{{Auth.UserId}}"))));
        Assert.Equal([ValidationRuleCheck.CommaInSingleValue], Check(Filtered(ValidationType.Reference, Fixed("Name", PolicyOperator.EQUAL, "a,b"))));
    }

    // Dates relative to now (section 3): {{Now}}, {{Now-18y}}, {{Now+30d}}; units y, mo, d, h, min.
    private static ValidationRule Relative(ValidationType type, object value, object? secondary = null, int version = 1) =>
        new() { Type = type, Value = value, SecondaryValue = secondary, EvaluationVersion = version };

    [Fact]
    public void Now_OnADateComparison_IsAccepted()
    {
        Assert.Empty(Check(Relative(ValidationType.GreaterThan, "{{Now}}"), "BirthDate"));
        Assert.Empty(Check(Relative(ValidationType.LessThanOrEqual, "{{Now-18y}}"), "BirthDate"));
        Assert.Empty(Check(Relative(ValidationType.GreaterThanOrEqual, "{{Now+2h}}"), "BirthDate"));
        Assert.Empty(Check(Relative(ValidationType.LessThan, "{{Now-15min}}"), "BirthDate"));
        Assert.Empty(Check(Relative(ValidationType.Range, "{{Now-1mo}}", "{{Now+30d}}"), "BirthDate"));
        Assert.Empty(Check(Relative(ValidationType.Range, "2025-01-01", "{{Now}}"), "BirthDate"));
    }

    [Fact]
    public void Now_OnlyOnDateTimeProperties_AndOnlyInGreaterLessAndRange()
    {
        Assert.Equal([ValidationRuleCheck.NowOnlyForDates], Check(Relative(ValidationType.GreaterThan, "{{Now}}"), "Age"));
        // Equal and Not equal would never pass: now moves on every save.
        Assert.Equal([ValidationRuleCheck.NowOnlyForComparisons], Check(Relative(ValidationType.Equal, "{{Now}}"), "BirthDate"));
        Assert.Equal([ValidationRuleCheck.NowOnlyForComparisons], Check(Relative(ValidationType.NotEqual, "{{Now-1d}}"), "BirthDate"));
    }

    [Theory]
    [InlineData("{{Now+x}}")]
    [InlineData("{{Now-18}}")]
    [InlineData("{{Now-18w}}")]
    [InlineData("{{Now-0d}}")]
    [InlineData("{{Now--1d}}")]
    public void Now_InAnotherShape_IsRefused_WithTheExpectedShape(string value)
    {
        Assert.Equal([ValidationRuleCheck.InvalidNow(value)], Check(Relative(ValidationType.GreaterThan, value), "BirthDate"));
    }

    [Fact]
    public void Now_InAVersionZeroRule_StaysText_SoAnExistingRuleIsUntouched()
    {
        Assert.Empty(Check(Relative(ValidationType.Equal, "{{Now}}", version: 0), "Age"));
    }
}
