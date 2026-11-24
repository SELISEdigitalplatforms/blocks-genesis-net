#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using static XUnitTest.DataAccess.Lookups.AccessLookupFixtures;

namespace XUnitTest.DataAccess.Rules;

public sealed class PolicyLookupRuleCheckTests
{
    private static DataAccessPolicy Allow(params PolicyRule[] rules) => Rls(PolicyOperation.READ, rules);

    [Fact]
    public void Shape_DenyPolicy_Refused()
    {
        var policy = Allow(MyDepartment());
        policy.IsAllowPolicy = false;
        Assert.Contains("Exists in another schema is only available in rule sets that allow access.", PolicyLookupRuleCheck.Shape(policy));
    }

    [Fact]
    public void Shape_AllowPolicy_NoMessages() =>
        Assert.Empty(PolicyLookupRuleCheck.Shape(Allow(MyDepartment(), MyDepartment(PolicyOperator.NOT_EXISTS_IN))));

    [Fact]
    public void Shape_MissingReference_Refused()
    {
        var rule = MyDepartment();
        rule.Reference = null;
        Assert.Equal(["The exists-in rule on 'DepartmentId' has no schema to look into."], PolicyLookupRuleCheck.Shape(Allow(rule)));
    }

    [Fact]
    public void Shape_ReferenceOnOldOperator_Refused()
    {
        var rule = DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Name", PolicyOperator.EQUAL, staticValue: "a");
        rule.Reference = new PolicyReference { SchemaName = "Department" };
        Assert.Equal(["A rule on 'Name' carries a lookup target, but only exists-in rules can."], PolicyLookupRuleCheck.Shape(Allow(rule)));
    }

    [Fact]
    public void Shape_WhereRightSideFromField_Refused()
    {
        var where = DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "UserId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "Name");
        Assert.Equal(["The right side of a lookup's WHERE condition on 'UserId' must be a fixed value or a value of the current user."],
            PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, where))));
    }

    [Fact]
    public void Shape_WhereLeftSideNotSchemaField_Refused()
    {
        var where = DataAccessTestSupport.Rule(ConditionSource.AUTH, "userId", PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: "x");
        Assert.Equal(["The left side of a lookup's WHERE condition on 'userId' must be a field of the schema looked into."],
            PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, where))));
    }

    [Theory]
    [InlineData("UserId", true)]
    [InlineData("EMAIL", true)]
    [InlineData("Nonsense", false)]
    public void Shape_WhereAuthName(string name, bool accepted)
    {
        var result = PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, WhereAuth("UserId", name))));
        Assert.Equal(accepted, result.Count == 0);
    }

    [Fact]
    public void Shape_WhereStaticAuthTemplateWithUnknownName_Refused()
    {
        Assert.NotEmpty(PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, WhereStatic("UserId", "{{auth.Nope}}")))));
        Assert.Empty(PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, WhereStatic("UserId", "{{token.UserId}}")))));
    }

    [Fact]
    public void Shape_WhereBadRegex_Refused()
    {
        var where = DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Name", PolicyOperator.REGEX, staticValue: "([");
        var result = PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, where)));
        Assert.Contains("This pattern isn't valid: ([", result);
    }

    [Fact]
    public void Shape_WhereNestedLookup_Refused()
    {
        var inner = Lookup("DepartmentId", "Department", "Code");
        var result = PolicyLookupRuleCheck.Shape(Allow(Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.EXISTS_IN, inner)));
        Assert.Contains("Exists in another schema can't be used inside a lookup's WHERE box.", result);
    }

    private static PolicyRuleGroup Nest(int levels, PolicyRule leaf)
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, leaf);
        for (var i = 1; i < levels; i++)
        {
            var outer = DataAccessTestSupport.Group(PolicyLogicalOperator.AND);
            outer.NestedGroups.Add(group);
            group = outer;
        }
        return group;
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Shape_WhereDepth(int levels, bool allowed)
    {
        var rule = Lookup("DepartmentId", "Employee", "DepartmentId");
        rule.Reference!.Filter = Nest(levels, WhereAuth("UserId", "UserId"));
        var result = PolicyLookupRuleCheck.Shape(Allow(rule));
        Assert.Equal(allowed, result.Count == 0);
        if (!allowed)
            Assert.Contains("A lookup's WHERE box can be nested at most 3 levels deep.", result);
    }

    [Fact]
    public void Targets_InvalidReasonsReturned()
    {
        var rule = Lookup("DepartmentId", "Nowhere", "Code");
        var employee = Employee(Allow(rule));
        var targets = AccessLookupTargets.Build(employee, [employee]);
        var result = PolicyLookupRuleCheck.Targets(employee.Policies[0], employee, targets);
        Assert.Single(result);
        Assert.Contains("Nowhere", result[0]);
    }

    [Fact]
    public void Targets_ValidTarget_NoMessages()
    {
        var employee = Employee(Allow(MyDepartment()));
        var targets = AccessLookupTargets.Build(employee, [employee, Department()]);
        Assert.Empty(PolicyLookupRuleCheck.Targets(employee.Policies[0], employee, targets));
    }

    [Fact]
    public void Targets_NotBuilt_CantBeFound()
    {
        var employee = Employee(Allow(MyDepartment()));
        var targets = AccessLookupTargets.Build(Employee(), []);
        Assert.Equal(["Employee.DepartmentId can't be found."], PolicyLookupRuleCheck.Targets(employee.Policies[0], employee, targets));
    }

    [Fact]
    public void Targets_SystemPolicyIntoTenantSchema_Refused()
    {
        var user = User(Allow(Lookup("Email", "Department", "Name")));
        var targets = AccessLookupTargets.Build(user, [user, Department()]);
        Assert.Equal(["A rule on a system schema can only look into system schemas."], PolicyLookupRuleCheck.Targets(user.Policies[0], user, targets));
    }

    [Theory]
    [InlineData(CrossSchemaAccessLevel.Off, false, true)]
    [InlineData(CrossSchemaAccessLevel.TenantSchemas, false, false)]
    [InlineData(CrossSchemaAccessLevel.TenantSchemas, true, true)]
    [InlineData(CrossSchemaAccessLevel.AllSchemas, true, false)]
    [InlineData(CrossSchemaAccessLevel.AllSchemas, false, false)]
    public void Level_Refusal(CrossSchemaAccessLevel level, bool onSystem, bool refused)
    {
        var result = PolicyLookupRuleCheck.Level(Allow(MyDepartment()), onSystem, level);
        Assert.Equal(refused ? "Exists in another schema isn't available yet on this platform." : null, result);
    }

    [Fact]
    public void Level_NoLookupRule_Null() =>
        Assert.Null(PolicyLookupRuleCheck.Level(Allow(DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Name", PolicyOperator.EQUAL, staticValue: "a")), true, CrossSchemaAccessLevel.Off));

    [Fact]
    public void ExistingPolicy_NoMessages()
    {
        var rules = Enum.GetValues<PolicyOperator>()
            .Where(o => o is not (PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN))
            .Select(o => DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Name", o, staticValue: "a"))
            .ToArray();
        Assert.Equal(15, rules.Length);
        var group = Nest(5, rules[0]);
        group.Rules.AddRange(rules);
        var policy = Rls(PolicyOperation.READ);
        policy.RuleGroup = group;
        policy.IsAllowPolicy = false;

        var employee = Employee(policy);
        var targets = AccessLookupTargets.Build(employee, [employee]);
        Assert.Empty(PolicyLookupRuleCheck.Shape(policy));
        Assert.Empty(PolicyLookupRuleCheck.Targets(policy, employee, targets));
        Assert.Null(PolicyLookupRuleCheck.Level(policy, true, CrossSchemaAccessLevel.Off));
    }

    [Fact]
    public void AcceptedAuthNames_Exactly()
    {
        Assert.Equal(12, PolicyLookupRuleCheck.AcceptedAuthNames.Count);
        Assert.Contains("UserId", PolicyLookupRuleCheck.AcceptedAuthNames);
    }
}
