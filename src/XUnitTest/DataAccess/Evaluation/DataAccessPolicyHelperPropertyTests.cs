using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using FsCheck.Xunit;

namespace XUnitTest.DataAccess.Evaluation;

// I04-WP5 property tests for DataAccessPolicyHelper. Each property states an invariant that must hold for every
// generated input. A failing property keeps its FsCheck seed in the output; that is a finding to report, not a test to weaken.
public class DataAccessPolicyHelperPropertyTests
{
    private static readonly string[] Identifiers = ["Courses", "Title", "Contact", "Email", "Skills", "Author", "Id"];

    [Property]
    public bool EqualityGivesTheSameAnswerEveryTime_ForAnyTwoStrings(string left, string right)
    {
        var first = DataAccessPolicyHelper.EvaluateCondition(left, PolicyOperator.EQUAL, right);
        var again = DataAccessPolicyHelper.EvaluateCondition(left, PolicyOperator.EQUAL, right);
        return first == again;
    }

    [Property]
    public bool NoPolicy_GrantsNoRow_ForAnyOperationAndPolicyType(int operationSeed, int typeSeed)
    {
        var operations = Enum.GetValues<PolicyOperation>();
        var types = Enum.GetValues<PolicyType>();
        var operation = operations[(operationSeed & int.MaxValue) % operations.Length];
        var type = types[(typeSeed & int.MaxValue) % types.Length];

        var result = new List<DataAccessPolicy>().EvaluatePolicies(AccessPrincipal.Anonymous, operation, type);

        return !result.IsAccessGranted;
    }

    [Property]
    public bool RowPath_WithNumericIndexSuffix_MatchesItsSchemaPath_AndNamedSuffixDoesNot(int seed, int index)
    {
        var depth = 1 + ((seed & int.MaxValue) % 3);
        var schemaPath = string.Join('.', Enumerable.Range(0, depth)
            .Select(i => Identifiers[((seed & int.MaxValue) % Identifiers.Length + i * 5) % Identifiers.Length]));
        var numericRowPath = $"{schemaPath}.{(index & int.MaxValue) % 100}";
        var namedRowPath = $"{schemaPath}.Extra";

        return DataAccessPolicyHelper.RowPathMatchesSchemaPath(schemaPath, numericRowPath)
            && !DataAccessPolicyHelper.RowPathMatchesSchemaPath(schemaPath, namedRowPath);
    }
}
