using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// A row rule whose schema field runs through a list of objects (<c>userId == Signatories.UserId</c>). The read
/// filter lets MongoDB match it against any item, so the in-memory check that masks the row's fields must give the
/// same answer; before, the path read as null there and a row the filter admitted came back with every value nulled.
/// </summary>
public class ArrayPathRowRuleTests
{
    private const string SchemaId = "schema-document";
    private const string Owner = "owner-1";
    private const string Signer = "signer-1";

    private static readonly IDataAccessPolicyResolutionService Resolver = new DataAccessPolicyResolutionService(new SchemaPolicySource());

    private static AccessPrincipal As(string userId) =>
        new(userId, $"{userId}@x.com", "tenant-1", "org-1", ["user"], [], true, false) { HasContext = true };

    #region The reported case: a signer who is not the owner

    [Fact]
    public void MaskRow_SignerListedInAnArrayOfObjects_SeesTheValues()
    {
        var served = MaskDocument(As(Signer));

        Assert.Equal("Contract", served["Title"]);
        Assert.Equal("RolledOut", served["DocumentStatus"]);
        var signatories = Assert.IsAssignableFrom<IList<object>>(served["Signatories"]);
        Assert.Equal(Signer, ((IDictionary<string, object>)signatories[1])["UserId"]);
    }

    [Fact]
    public void MaskRow_OwnerStillSeesTheValues()
    {
        var served = MaskDocument(As(Owner));

        Assert.Equal("Contract", served["Title"]);
        Assert.Equal("RolledOut", served["DocumentStatus"]);
    }

    [Fact]
    public void MaskRow_CallerNotInTheList_StillGetsNulls()
    {
        var served = MaskDocument(As("stranger"));

        Assert.Null(served["Title"]);
        Assert.Null(served["DocumentStatus"]);
    }

    [Fact]
    public void RowSatisfiesPolicy_UserIdEqualsArrayPath_HoldsForAListedCaller()
    {
        var policy = Policy(PolicyType.RLS, PolicyOperation.READ, [], Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.AUTH, "userId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "Items.UserId")));
        var row = new BsonDocument("Items", new BsonArray { new BsonDocument("UserId", "other"), new BsonDocument("UserId", "user-1") });

        Assert.True(QueryPolicyRuleEvaluator.RowSatisfiesPolicy(User(), policy, row.ToDictionary()));
    }

    private static Dictionary<string, object> MaskDocument(AccessPrincipal principal)
    {
        var schema = AssembleDocumentSchema(principal);
        var clsPolicies = schema.Policies.Where(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.READ).ToList();
        Assert.NotEmpty(clsPolicies);

        var request = new SchemaAccessRequest(principal, schema, PolicyOperation.READ);
        var row = new BsonDocument
        {
            { "_id", "doc-1" },
            { "Title", "Contract" },
            { "DocumentStatus", "RolledOut" },
            { "CreatedBy", Owner },
            { "OrganizationId", "org-1" },
            { "Signatories", new BsonArray
                {
                    new BsonDocument { { "UserId", Owner }, { "Email", "owner@x.com" } },
                    new BsonDocument { { "UserId", Signer }, { "Email", "signer@x.com" } },
                }
            },
        };

        // The gateway's own filter admits the row for the signer: the masking must agree with it.
        var rls = schema.Policies.Where(p => p.PolicyType == PolicyType.RLS).ToList()
            .EvaluatePolicies(principal, PolicyOperation.READ, PolicyType.RLS);
        var admitted = rls.IsAccessGranted && (!rls.RequiresDataFilter || BsonFilterMatcher.Matches(row, rls.DataFilter));
        Assert.Equal(principal.UserId != "stranger", admitted);

        return Resolver.MaskRow(request, row.ToDictionary(), new ReadProjection(new BsonDocument(), [], clsPolicies));
    }

    private static SchemaDefinitionExtended AssembleDocumentSchema(AccessPrincipal principal)
    {
        FieldDefinition F(string name, string type = "String", bool isArray = false, bool reference = false) => new()
        {
            Name = name,
            Type = type,
            IsArray = isArray,
            ReadAccessLevel = SchemaAccessLevel.Inherited,
            IsReferenceField = reference,
            ReferenceFieldType = reference ? "Signatory" : string.Empty,
        };

        List<FieldDefinition> fields =
        [
            F("Title"), F("DocumentStatus"), F("CreatedBy"), F("OrganizationId"),
            F("Signatories", "Signatory", isArray: true), F("Signatories.UserId", reference: true), F("Signatories.Email", reference: true),
        ];

        PolicyRule SameOrganization() =>
            Rule(ConditionSource.AUTH, "organizationId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "OrganizationId");
        DataAccessPolicy Rls(string name, PolicyRuleGroup group)
        {
            var policy = Policy(PolicyType.RLS, PolicyOperation.READ, [], group);
            policy.PolicyName = name;
            policy.ItemId = Guid.NewGuid().ToString();
            policy.SchemaId = SchemaId;
            policy.SchemaName = "Document";
            return policy;
        }

        var policies = new List<DataAccessPolicy>
        {
            Rls("Document read R1", Group(PolicyLogicalOperator.AND,
                Rule(ConditionSource.AUTH, "userId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "CreatedBy"),
                SameOrganization())),
            Rls("Document read R2", Group(PolicyLogicalOperator.AND,
                Rule(ConditionSource.AUTH, "userId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "Signatories.UserId"),
                SameOrganization(),
                Rule(ConditionSource.SCHEMA_FIELD, "DocumentStatus", PolicyOperator.NOT_EQUAL, ConditionSource.STATIC_VALUE, staticValue: "Draft"))),
        };

        // The same steps DataAccessResolver (and the gateway's schema build) run per schema.
        var schema = new SchemaDefinitionExtended
        {
            ItemId = SchemaId,
            SchemaName = "Document",
            CollectionName = "Documents",
            SchemaType = SchemaType.Entity,
            ReadAccessLevel = SchemaAccessLevel.Custom,
            Fields = FieldDefinitionMapper.GetFieldDefinitionResponses(fields, policies, []),
            Policies = policies,
        };
        SchemaPolicyDerivation.SetFieldsPolicies(schema, schema.Fields, policies, SchemaId, string.Empty, principal);
        SchemaPolicyDerivation.CleanupClsPoliciesBaseOnSchemaAccessLevel(schema);
        return schema;
    }

    #endregion

    #region The in-memory answer agrees with the read filter

    private static readonly Dictionary<string, BsonDocument> Rows = new()
    {
        ["caller listed"] = new BsonDocument("Items", new BsonArray { Item("other", 1), Item("user-1", 9) }),
        ["caller not listed"] = new BsonDocument("Items", new BsonArray { Item("other", 1), Item("user-2", 2) }),
        ["only the caller"] = new BsonDocument("Items", new BsonArray { Item("user-1", 3) }),
        ["empty list"] = new BsonDocument("Items", new BsonArray()),
        ["item value null"] = new BsonDocument("Items", new BsonArray { new BsonDocument { { "UserId", BsonNull.Value }, { "Score", 4 } } }),
        ["list in a list, listed"] = new BsonDocument("Groups", new BsonArray
        {
            new BsonDocument("Members", new BsonArray { Item("other", 1) }),
            new BsonDocument("Members", new BsonArray { Item("user-1", 7) }),
        }),
        ["list in a list, not listed"] = new BsonDocument("Groups", new BsonArray
        {
            new BsonDocument("Members", new BsonArray { Item("other", 1) }),
        }),
    };

    private static BsonDocument Item(string userId, int score) => new() { { "UserId", userId }, { "Score", score } };

    private static readonly Dictionary<string, Func<string, PolicyRule>> Rules = new()
    {
        ["userId == path"] = path => Rule(ConditionSource.AUTH, "userId", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, path + ".UserId"),
        ["path == userId"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".UserId", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId"),
        ["userId != path"] = path => Rule(ConditionSource.AUTH, "userId", PolicyOperator.NOT_EQUAL, ConditionSource.SCHEMA_FIELD, path + ".UserId"),
        ["path != userId"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".UserId", PolicyOperator.NOT_EQUAL, ConditionSource.AUTH, "userId"),
        ["path in list"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".UserId", PolicyOperator.IN, ConditionSource.STATIC_VALUE, staticValue: "user-1,user-3"),
        ["path not in list"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".UserId", PolicyOperator.NOT_IN, ConditionSource.STATIC_VALUE, staticValue: "user-1,user-3"),
        ["path > number"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".Score", PolicyOperator.GREATER_THAN, ConditionSource.STATIC_VALUE, staticValue: "5"),
        ["path <= number"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".Score", PolicyOperator.LESS_THAN_OR_EQUAL, ConditionSource.STATIC_VALUE, staticValue: "3"),
        ["path is null"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".UserId", PolicyOperator.IS_NULL),
        ["path is not null"] = path => Rule(ConditionSource.SCHEMA_FIELD, path + ".UserId", PolicyOperator.IS_NOT_NULL),
    };

    public static TheoryData<string, string> RulesAndRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var rule in Rules.Keys)
            foreach (var row in Rows.Keys)
                data.Add(rule, row);
        return data;
    }

    [Theory]
    [MemberData(nameof(RulesAndRows))]
    public void RowSatisfiesPolicy_AgreesWithTheReadFilter(string ruleName, string rowName)
    {
        var row = Rows[rowName];
        var path = row.Contains("Groups") ? "Groups.Members" : "Items";
        var policy = Policy(PolicyType.RLS, PolicyOperation.READ, [], Group(PolicyLogicalOperator.AND, Rules[ruleName](path)));
        var principal = User();

        var filter = policy.EvaluatePolicy(principal);
        Assert.True(filter.RequiresDataFilter, filter.ToJson());

        var inDatabase = BsonFilterMatcher.Matches(row, filter.DataFilter);
        var inMemory = QueryPolicyRuleEvaluator.RowSatisfiesPolicy(principal, policy, row.ToDictionary());

        Assert.True(inDatabase == inMemory, $"filter {filter.DataFilter.ToJson()} gives {inDatabase}, the row check gives {inMemory}");
    }

    #endregion

    #region Reading a path through lists

    [Fact]
    public void TryGetValuesThroughLists_ReadsEveryItem()
    {
        var row = Rows["caller listed"].ToDictionary();

        Assert.True(QueryPolicyRuleEvaluator.TryGetValuesThroughLists(row, "Items.UserId", out var values));
        Assert.Equal(["other", "user-1"], values);
    }

    [Fact]
    public void TryGetValuesThroughLists_ANumberSegmentPicksThatItem()
    {
        var row = Rows["caller listed"].ToDictionary();

        Assert.True(QueryPolicyRuleEvaluator.TryGetValuesThroughLists(row, "Items.1.UserId", out var values));
        Assert.Equal(["user-1"], values);
    }

    [Fact]
    public void TryGetValuesThroughLists_AnItemWithoutTheField_GivesNull()
    {
        var row = new BsonDocument("Items", new BsonArray { Item("user-1", 1), new BsonDocument("Email", "x@x.com") }).ToDictionary();

        Assert.True(QueryPolicyRuleEvaluator.TryGetValuesThroughLists(row, "Items.UserId", out var values));
        Assert.Equal(["user-1", null], values);
    }

    [Fact]
    public void TryGetValuesThroughLists_ReadsBsonValuesInTheRow()
    {
        var row = new Dictionary<string, object> { ["Items"] = new BsonArray { Item("user-1", 1), Item("user-2", 2) } };

        Assert.True(QueryPolicyRuleEvaluator.TryGetValuesThroughLists(row, "Items.UserId", out var values));
        Assert.Equal(["user-1", "user-2"], values);
    }

    [Theory]
    [InlineData("Contact.Email")]
    [InlineData("Tags")]
    [InlineData("Contact.Tags")]
    [InlineData("Missing.Value")]
    public void TryGetValuesThroughLists_PathWithoutAListToEnter_IsLeftToTheSingleValue(string path)
    {
        var row = new BsonDocument
        {
            { "Contact", new BsonDocument { { "Email", "a@x.com" }, { "Tags", new BsonArray { "a", "b" } } } },
            { "Tags", new BsonArray { "a", "b" } },
        }.ToDictionary();

        Assert.False(QueryPolicyRuleEvaluator.TryGetValuesThroughLists(row, path, out var values));
        Assert.Empty(values);
    }

    [Fact]
    public void IsNull_AnItemWithoutTheField_Holds()
    {
        // MongoDB's { "Items.UserId": null } matches when any item has no UserId.
        var policy = Policy(PolicyType.RLS, PolicyOperation.READ, [], Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.SCHEMA_FIELD, "Items.UserId", PolicyOperator.IS_NULL)));
        var row = new BsonDocument("Items", new BsonArray { Item("user-1", 1), new BsonDocument("Email", "x@x.com") });

        Assert.True(QueryPolicyRuleEvaluator.RowSatisfiesPolicy(User(), policy, row.ToDictionary()));
    }

    [Fact]
    public void AuthOnTheLeftWithIsNull_StillTestsTheAuthValue()
    {
        // Is null tests the left operand only; a list on the right must not change the answer.
        var policy = Policy(PolicyType.RLS, PolicyOperation.READ, [], Group(PolicyLogicalOperator.AND,
            Rule(ConditionSource.AUTH, "userId", PolicyOperator.IS_NULL, ConditionSource.SCHEMA_FIELD, "Items.UserId")));

        Assert.False(QueryPolicyRuleEvaluator.RowSatisfiesPolicy(User(), policy, Rows["empty list"].ToDictionary()));
    }

    #endregion
}
