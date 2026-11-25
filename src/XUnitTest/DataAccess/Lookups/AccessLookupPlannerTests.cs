using Blocks.Genesis.DataAccess.Evaluation;
#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Lookups;

public sealed class AccessLookupPlannerTests
{
    private static PolicyRule Lookup(PolicyOperator op = PolicyOperator.EXISTS_IN) => new()
    {
        LeftSource = ConditionSource.SCHEMA_FIELD,
        LeftOperand = "EmployeeId",
        Operator = op,
        Reference = new PolicyReference { SchemaName = "Employee" },
    };

    private static DataAccessPolicy PolicyOf(PolicyOperation operation, PolicyRuleGroup group) =>
        DataAccessTestSupport.Policy(PolicyType.RLS, operation, [], group);

    [Fact]
    public void UsesLookups_FalseForEveryExistingOperator()
    {
        var rules = Enum.GetValues<PolicyOperator>()
            .Where(op => op is not (PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN))
            .Select(op => new PolicyRule { LeftOperand = "A", Operator = op, StaticValue = "x" })
            .ToArray();
        var schema = DataAccessTestSupport.Schema(policies:
            [PolicyOf(PolicyOperation.READ, DataAccessTestSupport.Group(PolicyLogicalOperator.OR, rules))]);

        Assert.False(AccessLookupPlanner.UsesLookups(schema));
    }

    [Fact]
    public void UsesLookups_TrueForNestedExistsIn()
    {
        var inner = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, Lookup());
        var outer = new PolicyRuleGroup { NestedGroups = [new PolicyRuleGroup { NestedGroups = [inner] }] };
        var schema = DataAccessTestSupport.Schema(policies: [PolicyOf(PolicyOperation.READ, outer)]);

        Assert.True(AccessLookupPlanner.UsesLookups(schema));
        Assert.True(AccessLookupPlanner.UsesLookups(schema)); // cached
    }

    [Fact]
    public void RulesFor_OnlyMatchingOperationsAndAll()
    {
        var read = Lookup();
        var write = Lookup(PolicyOperator.NOT_EXISTS_IN);
        var all = Lookup();
        var plain = new PolicyRule { LeftOperand = "A", StaticValue = "x" };
        var schema = DataAccessTestSupport.Schema(policies:
        [
            PolicyOf(PolicyOperation.READ, DataAccessTestSupport.Group(PolicyLogicalOperator.AND, read, plain)),
            PolicyOf(PolicyOperation.WRITE, DataAccessTestSupport.Group(PolicyLogicalOperator.AND, write)),
            PolicyOf(PolicyOperation.ALL, new PolicyRuleGroup { NestedGroups = [DataAccessTestSupport.Group(PolicyLogicalOperator.AND, all)] }),
        ]);

        var forRead = AccessLookupPlanner.RulesFor(schema, [PolicyOperation.READ]);
        Assert.Equal(new[] { read, all }, forRead);

        var forWrite = AccessLookupPlanner.RulesFor(schema, [PolicyOperation.WRITE]);
        Assert.Equal(new[] { write, all }, forWrite);

        Assert.Equal(new[] { all }, AccessLookupPlanner.RulesFor(schema, [PolicyOperation.DELETE]));
    }
    private static bool Cached(SchemaDefinitionExtended schema)
    {
        var table = (System.Runtime.CompilerServices.ConditionalWeakTable<SchemaDefinitionExtended, object>)typeof(AccessLookupPlanner)
            .GetField("UsesCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        return table.TryGetValue(schema, out _);
    }

    [Fact]
    public void HasLookups_SameAnswerAsUsesLookups_WithoutCaching()
    {
        var inner = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, Lookup(PolicyOperator.NOT_EXISTS_IN));
        var withLookup = DataAccessTestSupport.Schema(policies: [PolicyOf(PolicyOperation.READ, new PolicyRuleGroup { NestedGroups = [inner] })]);
        var without = DataAccessTestSupport.Schema(policies:
            [PolicyOf(PolicyOperation.READ, DataAccessTestSupport.Group(PolicyLogicalOperator.AND, new PolicyRule { LeftOperand = "A", StaticValue = "x" }))]);

        Assert.True(AccessLookupPlanner.HasLookups(withLookup));
        Assert.False(AccessLookupPlanner.HasLookups(without));
        // Per-request schema instances leave no cache entry behind.
        Assert.False(Cached(withLookup));
        Assert.False(Cached(without));

        Assert.True(AccessLookupPlanner.UsesLookups(withLookup));
        Assert.True(Cached(withLookup));
    }
}
