using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// Variables and the rule parts added by the validation and access rules plan (phases 1–2). Kept apart from
/// <see cref="ValidationVariablesTests"/>, which must stay unmodified (G1).
/// </summary>
public class ValidationVariablesNewRulesTests
{
    private static readonly DateTime Jan10 = new(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ResolvingAVariable_KeepsEveryOtherPartOfTheRule()
    {
        // The rule is rebuilt with the values the variables read; a part left behind (an "only when" condition,
        // a reference filter) would silently change what the rule checks.
        var when = new PolicyRuleGroup { Rules = [new() { LeftOperand = "Country", StaticValue = "DE" }] };
        var filter = new PolicyRuleGroup { Rules = [new() { LeftOperand = "Status", StaticValue = "Active" }] };
        var rule = new ValidationRule
        {
            Type = ValidationType.Range, Value = "{{Employee.StartDate}}", SecondaryValue = "{{Employee.EndDate}}",
            IsActive = false, EvaluationVersion = 1, When = when, TargetFilter = filter,
        };

        var resolved = DataValidationHelper.WithValues(rule, Jan10, Jan10.AddDays(1), "Out of range");

        Assert.Equal(ValidationType.Range, resolved.Type);
        Assert.Equal(Jan10, resolved.Value);
        Assert.Equal(Jan10.AddDays(1), resolved.SecondaryValue);
        Assert.Equal("Out of range", resolved.ErrorMessage);
        Assert.False(resolved.IsActive);
        Assert.Equal(1, resolved.EvaluationVersion);
        Assert.Same(when, resolved.When);
        Assert.Same(filter, resolved.TargetFilter);
    }

    [Theory]
    [InlineData("{{Now}}")]
    [InlineData("{{Now-18y}}")]
    [InlineData("{{Now+30d}}")]
    public void DateRelativeToNow_IsNotAVariable_SoItIsLeftToTheRuleCheck(string value)
    {
        // Before, "{{Now}}" failed here with "Missing prefix" and "{{Now-18y}}" with "Invalid name".
        var rule = new ValidationRule { Type = ValidationType.GreaterThan, Value = value, EvaluationVersion = 1 };
        Assert.Null(ValidationVariableCheck.Check(rule, "DateTime", "Employee", new Dictionary<string, VariableField>()));
    }
}
