using System.Globalization;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// Rules checked against one row in memory (column masking, the write guard): the policy editor sends a typed
/// value as text, while the row holds a real bool, number or date, so the text is read as that type.
/// </summary>
public class TypedRowComparisonTests
{
    private static readonly DateTime JoinedOn = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<object, PolicyOperator, object, bool> Comparisons => new()
    {
        // Bools.
        { true, PolicyOperator.EQUAL, "true", true },
        { true, PolicyOperator.EQUAL, "TRUE", true },
        { true, PolicyOperator.EQUAL, " true ", true },
        { false, PolicyOperator.EQUAL, "true", false },
        { false, PolicyOperator.NOT_EQUAL, "true", true },
        { true, PolicyOperator.NOT_EQUAL, "true", false },
        { true, PolicyOperator.EQUAL, "yes", false },
        // Numbers, whatever their stored type.
        { 150, PolicyOperator.EQUAL, "150", true },
        { 150L, PolicyOperator.EQUAL, "150.0", true },
        { 150.5, PolicyOperator.EQUAL, "150.5", true },
        { 150m, PolicyOperator.EQUAL, "150", true },
        { 150L, PolicyOperator.EQUAL, "151", false },
        { 150L, PolicyOperator.NOT_EQUAL, "151", true },
        { 150L, PolicyOperator.EQUAL, "abc", false },
        // Dates, from an ISO date or date-time.
        { JoinedOn, PolicyOperator.EQUAL, "2025-01-01", true },
        { JoinedOn, PolicyOperator.EQUAL, "2025-01-01T00:00:00Z", true },
        { DateTime.SpecifyKind(JoinedOn, DateTimeKind.Unspecified), PolicyOperator.EQUAL, "2025-01-01", true },
        { JoinedOn, PolicyOperator.EQUAL, "2025-01-02", false },
        { JoinedOn, PolicyOperator.NOT_EQUAL, "2025-01-02", true },
        // Typed value on the right (a static value written on the left of the rule).
        { "true", PolicyOperator.EQUAL, true, true },
        { "150", PolicyOperator.EQUAL, 150L, true },
        // Text keeps comparing as text, ignoring case.
        { "Active", PolicyOperator.EQUAL, "active", true },
        // In lists, each item read the same way.
        { true, PolicyOperator.IN, "true,false", true },
        { 150L, PolicyOperator.IN, "10,150", true },
        { 150.5, PolicyOperator.IN, "10,150.50", true },
        { JoinedOn, PolicyOperator.IN, "2024-01-01,2025-01-01", true },
        { 150L, PolicyOperator.NOT_IN, "10,20", true },
        { 150L, PolicyOperator.NOT_IN, "10,150", false },
        // Ordering: numbers as numbers, read the same way whatever the server's culture.
        { 150, PolicyOperator.GREATER_THAN, "20", true },
        { 150.5, PolicyOperator.GREATER_THAN_OR_EQUAL, "150.5", true },
        { 150.5, PolicyOperator.LESS_THAN, "150.25", false },
        { 150m, PolicyOperator.LESS_THAN_OR_EQUAL, "1e3", true },
        // Ordering: dates as dates, not as their display text.
        { JoinedOn, PolicyOperator.GREATER_THAN_OR_EQUAL, "2025-01-01", true },
        { JoinedOn, PolicyOperator.GREATER_THAN, "2024-12-31T23:59:59Z", true },
        { JoinedOn, PolicyOperator.LESS_THAN, "2025-01-02", true },
        { JoinedOn, PolicyOperator.LESS_THAN, "2024-06-01", false },
        { DateTime.SpecifyKind(JoinedOn, DateTimeKind.Unspecified), PolicyOperator.LESS_THAN_OR_EQUAL, "2025-01-01", true },
        { "2025-01-01", PolicyOperator.LESS_THAN, JoinedOn.AddDays(1), true },
        // Ordering: text that is neither stays text order.
        { "b", PolicyOperator.GREATER_THAN, "a", true },
    };

    // A comma-decimal culture must not change how "150.5" is read.
    [Fact]
    public void NumberText_IsReadTheSameWayInEveryCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.True(DataAccessPolicyHelper.EvaluateCondition(150.5, PolicyOperator.GREATER_THAN, "150.25"));
            Assert.True(DataAccessPolicyHelper.EvaluateCondition(150.5, PolicyOperator.EQUAL, "150.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [MemberData(nameof(Comparisons))]
    public void ATypedRowValue_ComparesWithTheTextAsItsType(object left, PolicyOperator op, object right, bool expected)
    {
        Assert.Equal(expected, DataAccessPolicyHelper.EvaluateCondition(left, op, right));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ARuleOnABoolField_MatchesTheRow(bool stored, bool expected)
    {
        var rule = new PolicyRule
        {
            LeftSource = ConditionSource.SCHEMA_FIELD, LeftOperand = "Active", Operator = PolicyOperator.EQUAL,
            RightSource = ConditionSource.STATIC_VALUE, StaticValue = "true",
        };

        Assert.Equal(expected, QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(User(), rule, new Dictionary<string, object> { ["Active"] = stored }));
    }
}
