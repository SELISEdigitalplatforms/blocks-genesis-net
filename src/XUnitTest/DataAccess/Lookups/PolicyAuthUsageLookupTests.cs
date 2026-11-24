#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Lookups;

public sealed class PolicyAuthUsageLookupTests
{
    [Fact]
    public void Of_IncludesAuthNameUsedOnlyInLookupFilter()
    {
        var policy = new DataAccessPolicy
        {
            RuleGroup = new PolicyRuleGroup
            {
                Rules =
                [
                    new PolicyRule
                    {
                        LeftOperand = "OrgId",
                        Operator = PolicyOperator.EXISTS_IN,
                        Reference = new PolicyReference
                        {
                            SchemaName = "Orgs",
                            Filter = new PolicyRuleGroup
                            {
                                Rules =
                                [
                                    new PolicyRule
                                    {
                                        LeftOperand = "Role",
                                        Operator = PolicyOperator.EQUAL,
                                        RightSource = ConditionSource.AUTH,
                                        RightOperand = "Permissions",
                                    }
                                ]
                            }
                        }
                    }
                ]
            }
        };

        var usage = PolicyAuthUsage.Of([policy]);

        Assert.True(usage.UsesPermissions);
    }
}
