using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// <see cref="IDataAccessPolicyResolutionService.MaskRow"/> evaluates each rule group once per row
/// instead of once per policy. It must mask exactly as evaluating every policy does. The schema
/// mixes every kind of read policy (inherited copies of row-level policies, explicit column
/// policies over several fields, deny policies, ALL policies, OR and nested groups, token and row
/// operands), Custom fields without a policy, User fields, nested objects and arrays, and is
/// assembled the way <see cref="DataAccessResolver"/> assembles one, so the copies are the real ones.
/// </summary>
public class MaskRowEquivalenceTests
{
    private const string SchemaId = "schema-case";

    private static readonly IDataAccessPolicyResolutionService Resolver = new DataAccessPolicyResolutionService(new SchemaPolicySource());

    public static TheoryData<string> Callers() => new()
    {
        "firmadmin", "lawyer owner", "lawyer assigned", "lawyer stranger", "assistant", "auditor",
        "no permission", "other organization", "no roles", "anonymous", "no context", "root tenant",
    };

    [Theory]
    [MemberData(nameof(Callers))]
    public void MaskRow_MatchesEvaluatingEveryPolicy(string caller)
    {
        var principal = Principal(caller);
        var schema = AssembleCaseSchema(principal);
        var clsPolicies = schema.Policies
            .Where(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.READ)
            .ToList();
        Assert.True(clsPolicies.Count > 20, "the inherited copies must be in place for this test to mean anything");

        var request = new SchemaAccessRequest(principal, schema, PolicyOperation.READ);
        var projection = new ReadProjection(new BsonDocument(), ["Status"], principal.IsRootTenant ? [] : clsPolicies);

        foreach (var document in Rows())
        {
            var plain = principal.IsRootTenant
                ? document.ToDictionary()
                : QueryClsRowMaskingHelper.MaskRowByClsPolicies(
                    principal, document.ToDictionary(), schema, projection.ClsReadPolicies, projection.EvaluationOnlyFieldPaths,
                    (policy, row) => QueryPolicyRuleEvaluator.RowSatisfiesPolicy(principal, policy, row));
            var served = Resolver.MaskRow(request, document.ToDictionary(), projection);

            Assert.Equal(new BsonDocument(plain).ToJson(), new BsonDocument(served).ToJson());
        }
    }

    [Fact]
    public void MaskRow_TheOwnerStillSeesTheirRow()
    {
        var principal = Principal("lawyer owner");
        var schema = AssembleCaseSchema(principal);
        var clsPolicies = schema.Policies.Where(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.READ).ToList();
        var request = new SchemaAccessRequest(principal, schema, PolicyOperation.READ);

        var served = Resolver.MaskRow(request, Rows()[0].ToDictionary(), new ReadProjection(new BsonDocument(), [], clsPolicies));

        Assert.Equal("own case", served["Title"]);
        Assert.Equal("a@x.com", ((Dictionary<string, object>)served["Contact"])["Email"]);
    }

    [Fact]
    public void InheritedCopies_ShareTheirSourceRuleGroup()
    {
        // What makes evaluating once per rule group pay off: the copies made for every inherited
        // field point at the source policy's rule group rather than copies of it.
        var schema = AssembleCaseSchema(Principal("firmadmin"));
        var source = schema.Policies.Single(p => p.PolicyType == PolicyType.RLS && p.PolicyName == "owner");

        var copies = schema.Policies.Where(p => p.PolicyType == PolicyType.CLS && ReferenceEquals(p.RuleGroup, source.RuleGroup)).ToList();

        Assert.True(copies.Count > 10);
    }

    [Fact]
    public void OncePerRuleGroup_EvaluatesASharedRuleGroupOncePerRow()
    {
        var principal = User(roles: "lawyer");
        var shared = Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId"));
        var policies = Enumerable.Range(0, 30)
            .Select(i => Policy(PolicyType.CLS, PolicyOperation.READ, [$"F{i}"], shared))
            .ToList();
        var evaluate = QueryPolicyRuleEvaluator.OncePerRuleGroup(principal);

        var row = new CountingRow { ["CreatedBy"] = "user-1" };
        Assert.All(policies, p => Assert.True(evaluate(p, row)));
        Assert.Equal(1, row.Lookups);

        var otherRow = new CountingRow { ["CreatedBy"] = "user-2" };
        Assert.All(policies, p => Assert.False(evaluate(p, otherRow)));
        Assert.Equal(1, otherRow.Lookups);
    }

    [Fact]
    public void OncePerRuleGroup_KeepsDistinctRuleGroupsApart()
    {
        var principal = User(roles: "lawyer");
        var owner = Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId"));
        var someoneElse = Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: "user-2"));
        var evaluate = QueryPolicyRuleEvaluator.OncePerRuleGroup(principal);
        var row = new Dictionary<string, object> { ["CreatedBy"] = "user-1" };

        Assert.True(evaluate(Policy(PolicyType.CLS, PolicyOperation.READ, ["A"], owner), row));
        Assert.False(evaluate(Policy(PolicyType.CLS, PolicyOperation.READ, ["B"], someoneElse), row));
        Assert.False(evaluate(new DataAccessPolicy { RuleGroup = null!, FieldNames = ["C"] }, row));
    }

    [Fact]
    public void OncePerRuleGroup_DoesNotKeepAFailedEvaluation()
    {
        var principal = User();
        var invalidRegex = Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.SCHEMA_FIELD, "Name", PolicyOperator.REGEX, ConditionSource.STATIC_VALUE, staticValue: "(unclosed"));
        var policy = Policy(PolicyType.CLS, PolicyOperation.READ, ["Name"], invalidRegex);
        var evaluate = QueryPolicyRuleEvaluator.OncePerRuleGroup(principal);
        var row = new Dictionary<string, object> { ["Name"] = "x" };

        var plainFailure = Record.Exception(() => QueryPolicyRuleEvaluator.RowSatisfiesPolicy(principal, policy, row));
        var servedFailure = Record.Exception(() => evaluate(policy, row));
        var servedAgain = Record.Exception(() => evaluate(policy, row));

        Assert.Equal(plainFailure?.GetType(), servedFailure?.GetType());
        Assert.Equal(plainFailure?.GetType(), servedAgain?.GetType());
    }

    private static AccessPrincipal Principal(string caller)
    {
        AccessPrincipal As(string userId, string[] roles, string[] permissions, string organizationId = "org-1") =>
            new(userId, $"{userId}@x.com", "tenant-1", organizationId, roles, permissions, true, false) { HasContext = true };

        return caller switch
        {
            "firmadmin" => As("admin", ["firmadmin"], ["fe::cases"]),
            "lawyer owner" => As("u-1", ["lawyer"], ["fe::cases"]),
            "lawyer assigned" => As("u-3", ["lawyer"], ["fe::cases"]),
            "lawyer stranger" => As("u-9", ["lawyer"], ["fe::cases"]),
            "assistant" => As("u-2", ["assistant", "u-1"], ["fe::cases"]),
            "auditor" => As("aud", ["auditor"], ["fe::cases", "audit::read"]),
            "no permission" => As("u-1", ["lawyer"], []),
            "other organization" => As("admin", ["firmadmin"], ["fe::cases"], "org-2"),
            "no roles" => As("u-1", [], []),
            "anonymous" => new AccessPrincipal("", "", "", "", [], [], false, false) { HasContext = true },
            "no context" => AccessPrincipal.Anonymous,
            "root tenant" => As("root", ["firmadmin"], ["fe::cases"]) with { IsRootTenant = true },
            _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, null),
        };
    }

    private static SchemaDefinitionExtended AssembleCaseSchema(AccessPrincipal principal)
    {
        FieldDefinition F(string name, string type = "String", bool isArray = false,
            SchemaAccessLevel read = SchemaAccessLevel.Inherited, bool reference = false) => new()
        {
            Name = name,
            Type = type,
            IsArray = isArray,
            ReadAccessLevel = read,
            IsReferenceField = reference,
            ReferenceFieldType = reference ? "Contact" : string.Empty,
        };

        List<FieldDefinition> fields =
        [
            F("Title"), F("Status"), F("CreatedBy"), F("OrganizationId"), F("LawyerIds", isArray: true),
            F("Tags", isArray: true), F("Amount", "Float"), F("OpenedAt", "DateTime"),
            F("Secret", read: SchemaAccessLevel.Custom), F("Unguarded", read: SchemaAccessLevel.Custom),
            F("PublicNote", read: SchemaAccessLevel.Public), F("MemberNote", read: SchemaAccessLevel.User),
            F("Contact", "Contact"), F("Contact.Email", reference: true), F("Contact.Phone", reference: true),
            F("Parties", "Contact", isArray: true), F("Parties.Email", reference: true), F("Parties.Phone", reference: true),
        ];

        PolicyRule Auth(string claim, PolicyOperator op, string value) =>
            Rule(ConditionSource.AUTH, claim, op, ConditionSource.STATIC_VALUE, staticValue: value);
        PolicyRule SameOrganization() =>
            Rule(ConditionSource.AUTH, "organizationId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "OrganizationId");
        DataAccessPolicy Named(string name, DataAccessPolicy policy)
        {
            policy.PolicyName = name;
            policy.ItemId = Guid.NewGuid().ToString();
            policy.SchemaId = SchemaId;
            policy.SchemaName = "Case";
            return policy;
        }
        DataAccessPolicy Rls(string name, PolicyRuleGroup group, PolicyOperation operation = PolicyOperation.READ, bool allow = true) =>
            Named(name, Policy(PolicyType.RLS, operation, [], group, allow));
        DataAccessPolicy Cls(string name, string[] fieldNames, PolicyRuleGroup group, bool allow = true) =>
            Named(name, Policy(PolicyType.CLS, PolicyOperation.READ, fieldNames, group, allow));

        var policies = new List<DataAccessPolicy>
        {
            Rls("organization", Group(PolicyLogicalOperator.AND,
                SameOrganization(), Auth("roles", PolicyOperator.IN, "firmadmin,auditor"), Auth("permissions", PolicyOperator.CONTAIN, "fe::cases"))),
            Rls("owner", Group(PolicyLogicalOperator.AND,
                SameOrganization(), Auth("roles", PolicyOperator.CONTAIN, "lawyer"),
                Rule(ConditionSource.AUTH, "userId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "CreatedBy"),
                Auth("permissions", PolicyOperator.CONTAIN, "fe::cases"))),
            Rls("assigned", Group(PolicyLogicalOperator.AND,
                SameOrganization(),
                Rule(ConditionSource.SCHEMA_FIELD, "LawyerIds", PolicyOperator.CONTAIN, ConditionSource.AUTH, "userId"))),
            Rls("assistant of creator", Group(PolicyLogicalOperator.AND,
                Auth("roles", PolicyOperator.CONTAIN, "assistant"),
                Rule(ConditionSource.AUTH, "roles", PolicyOperator.CONTAIN, ConditionSource.SCHEMA_FIELD, "CreatedBy"))),
            Rls("open or audited", new PolicyRuleGroup
            {
                LogicalOperator = PolicyLogicalOperator.OR,
                Rules = [Rule(ConditionSource.SCHEMA_FIELD, "Status", PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: "open")],
                NestedGroups =
                [
                    Group(PolicyLogicalOperator.AND,
                        Auth("roles", PolicyOperator.CONTAIN, "auditor"),
                        Rule(ConditionSource.SCHEMA_FIELD, "Amount", PolicyOperator.GREATER_THAN, ConditionSource.STATIC_VALUE, staticValue: 100)),
                ],
            }),
            Rls("everyone, any operation", Group(PolicyLogicalOperator.AND, Auth("roles", PolicyOperator.CONTAIN, "systemadmin")), PolicyOperation.ALL),
            Rls("not archived", Group(PolicyLogicalOperator.AND,
                Rule(ConditionSource.SCHEMA_FIELD, "Status", PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: "archived")), allow: false),
            Rls("edit only", Group(PolicyLogicalOperator.AND, Auth("roles", PolicyOperator.CONTAIN, "firmadmin")), PolicyOperation.EDIT),
            Cls("secret for owners of open cases", ["Secret", "Contact.Phone"], Group(PolicyLogicalOperator.AND,
                Rule(ConditionSource.SCHEMA_FIELD, "Status", PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: "open"),
                Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId"))),
            Cls("secret denied to auditors", ["Secret"], Group(PolicyLogicalOperator.AND, Auth("roles", PolicyOperator.CONTAIN, "auditor")), allow: false),
            Cls("tagged parties", ["Parties"], Group(PolicyLogicalOperator.AND,
                Rule(ConditionSource.SCHEMA_FIELD, "Tags", PolicyOperator.CONTAIN, ConditionSource.STATIC_VALUE, staticValue: "shared"))),
        };

        // The same steps DataAccessResolver (and the gateway's schema build) run per schema.
        var schema = new SchemaDefinitionExtended
        {
            ItemId = SchemaId,
            SchemaName = "Case",
            CollectionName = "Cases",
            SchemaType = SchemaType.Entity,
            ReadAccessLevel = SchemaAccessLevel.Custom,
            Fields = FieldDefinitionMapper.GetFieldDefinitionResponses(fields, policies, []),
            Policies = policies,
        };
        SchemaPolicyDerivation.SetFieldsPolicies(schema, schema.Fields, policies.Where(p => p.PolicyType == PolicyType.RLS), SchemaId, string.Empty, principal);
        SchemaPolicyDerivation.CleanupClsPoliciesBaseOnSchemaAccessLevel(schema);
        return schema;
    }

    private static List<BsonDocument> Rows()
    {
        BsonDocument Contact(string email, string phone) => new() { { "Email", email }, { "Phone", phone } };

        return
        [
            new BsonDocument
            {
                { "_id", "c-1" }, { "Title", "own case" }, { "Status", "open" }, { "CreatedBy", "u-1" },
                { "OrganizationId", "org-1" }, { "LawyerIds", new BsonArray { "u-3" } }, { "Tags", new BsonArray { "shared", "vip" } },
                { "Amount", 250.5 }, { "OpenedAt", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
                { "Secret", "s-1" }, { "Unguarded", "u" }, { "PublicNote", "p" }, { "MemberNote", "m" },
                { "Contact", Contact("a@x.com", "111") },
                { "Parties", new BsonArray { Contact("b@x.com", "222"), Contact("c@x.com", "333") } },
            },
            new BsonDocument
            {
                { "_id", "c-2" }, { "Title", "closed case" }, { "Status", "closed" }, { "CreatedBy", "u-2" },
                { "OrganizationId", "org-1" }, { "LawyerIds", new BsonArray() }, { "Tags", new BsonArray { "internal" } },
                { "Amount", 50 }, { "Secret", "s-2" }, { "PublicNote", BsonNull.Value },
                { "Contact", Contact("d@x.com", "444") },
            },
            new BsonDocument
            {
                { "_id", "c-3" }, { "Title", "other firm" }, { "Status", "open" }, { "CreatedBy", "u-9" },
                { "OrganizationId", "org-2" }, { "Amount", 500L }, { "Secret", "s-3" }, { "MemberNote", "m-3" },
                { "Parties", new BsonArray { Contact("e@x.com", "555") } },
            },
            new BsonDocument
            {
                { "_id", "c-4" }, { "Title", "archived" }, { "Status", "archived" }, { "CreatedBy", "u-1" },
                { "OrganizationId", "org-1" }, { "LawyerIds", new BsonArray { "u-1", "u-3" } }, { "Amount", 150 },
            },
            new BsonDocument { { "_id", "c-5" }, { "Title", "bare" } },
        ];
    }

    /// <summary>A row that counts how often the evaluator reads a value from it.</summary>
    private sealed class CountingRow : Dictionary<string, object>, IDictionary<string, object>
    {
        public int Lookups { get; private set; }

        bool IDictionary<string, object>.TryGetValue(string key, out object value)
        {
            Lookups++;
            return TryGetValue(key, out value!);
        }
    }
}
