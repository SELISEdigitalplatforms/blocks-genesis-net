using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// Pins the policy resolver to the behaviour the data gateway had before the evaluation code moved out of it,
/// including two quirks kept on purpose: the READ gate honours ALL policies while the mutation gate ignores
/// them, and the root tenant bypasses everything.
/// </summary>
public class DataAccessPolicyResolutionServiceTests
{
    private static readonly IDataAccessPolicyResolutionService Resolver = new DataAccessPolicyResolutionService(new SchemaPolicySource());

    private static bool Granted(SchemaDefinitionExtended schema, PolicyOperation op, Blocks.Genesis.DataAccess.AccessPrincipal principal) =>
        Resolver.Check(new SchemaAccessRequest(principal, schema, op)).IsAccessGranted;

    [Fact]
    public void Check_Read_PublicSchema_Grants() => Assert.True(Granted(Schema(read: SchemaAccessLevel.Public), PolicyOperation.READ, User()));

    [Fact]
    public void Check_Read_CustomWithoutPolicies_Denies() => Assert.False(Granted(Schema(read: SchemaAccessLevel.Custom), PolicyOperation.READ, User()));

    [Fact]
    public void Check_Read_RootTenant_BypassesCustom() => Assert.True(Granted(Schema(read: SchemaAccessLevel.Custom), PolicyOperation.READ, User(root: true)));

    [Fact]
    public void Check_Read_AllOperationPolicy_Applies()
    {
        var schema = Schema(read: SchemaAccessLevel.Custom, policies: [RolePolicy(PolicyOperation.ALL, "admin")]);
        Assert.True(Granted(schema, PolicyOperation.READ, User(roles: "admin")));
        Assert.False(Granted(schema, PolicyOperation.READ, User(roles: "guest")));
    }

    [Fact]
    public void Check_Edit_AllOperationPolicy_IsIgnored_AsBefore()
    {
        var schema = Schema(edit: SchemaAccessLevel.Custom, policies: [RolePolicy(PolicyOperation.ALL, "admin")]);
        Assert.False(Granted(schema, PolicyOperation.EDIT, User(roles: "admin")));
    }

    [Fact]
    public void Check_Edit_EditPolicy_Evaluates()
    {
        var schema = Schema(edit: SchemaAccessLevel.Custom, policies: [RolePolicy(PolicyOperation.EDIT, "admin")]);
        Assert.True(Granted(schema, PolicyOperation.EDIT, User(roles: "admin")));
        Assert.False(Granted(schema, PolicyOperation.EDIT, User(roles: "guest")));
    }

    [Fact]
    public void Check_Write_NonCustomLevel_Grants() => Assert.True(Granted(Schema(write: SchemaAccessLevel.User), PolicyOperation.WRITE, User()));

    [Fact]
    public void BuildReadFilter_Custom_CombinesUserFilterWithRlsFilter()
    {
        var schema = Schema(read: SchemaAccessLevel.Custom, policies: [OwnerPolicy(PolicyOperation.READ)]);
        var request = new SchemaAccessRequest(User(), schema, PolicyOperation.READ);
        var rls = Resolver.Check(request);
        Assert.True(rls.IsAccessGranted);
        Assert.Equal(new BsonDocument("CreatedBy", "user-1").ToJson(), rls.DataFilter.ToJson());

        var filter = Resolver.BuildReadFilter(request, new BsonDocument("Name", "A"), rls);

        var and = filter["$and"].AsBsonArray;
        Assert.Equal(2, and.Count);
        Assert.Contains(new BsonDocument("CreatedBy", "user-1"), and);
    }

    [Fact]
    public void BuildReadFilter_Custom_EmptyUserFilter_ReturnsRlsFilterAlone()
    {
        var schema = Schema(read: SchemaAccessLevel.Custom, policies: [OwnerPolicy(PolicyOperation.READ)]);
        var request = new SchemaAccessRequest(User(), schema, PolicyOperation.READ);

        var filter = Resolver.BuildReadFilter(request, new BsonDocument(), Resolver.Check(request));

        Assert.Equal(new BsonDocument("CreatedBy", "user-1").ToJson(), filter.ToJson());
    }

    [Fact]
    public void BuildReadFilter_NonCustom_MapsItemIdToUnderscoreId()
    {
        var request = new SchemaAccessRequest(User(), Schema(read: SchemaAccessLevel.Public), PolicyOperation.READ);

        var filter = Resolver.BuildReadFilter(request, new BsonDocument("ItemId", "x"), Resolver.Check(request));

        Assert.Equal(new BsonDocument("_id", "x").ToJson(), filter.ToJson());
    }

    [Fact]
    public void ApplyMutationFilter_Custom_AndsRlsFilter_NonCustom_ReturnsBase()
    {
        var baseFilter = new BsonDocument("Name", "A");
        var rls = new PolicyEvaluationResult { IsAccessGranted = true, RequiresDataFilter = true, DataFilter = new BsonDocument("CreatedBy", "user-1") };

        var custom = Resolver.ApplyMutationFilter(new SchemaAccessRequest(User(), Schema(edit: SchemaAccessLevel.Custom), PolicyOperation.EDIT), baseFilter, rls);
        Assert.Equal(2, custom["$and"].AsBsonArray.Count);

        var open = Resolver.ApplyMutationFilter(new SchemaAccessRequest(User(), Schema(edit: SchemaAccessLevel.Public), PolicyOperation.EDIT), baseFilter, rls);
        Assert.Same(baseFilter, open);
    }

    [Fact]
    public void MaskRow_ClsPolicyNotSatisfied_NullsProtectedField_AndSatisfied_KeepsIt()
    {
        var schema = Schema(
            read: SchemaAccessLevel.Custom,
            fields: [Field("Name", read: SchemaAccessLevel.Public), Field("Salary", "Int", read: SchemaAccessLevel.Custom)],
            policies: [RolePolicy(PolicyOperation.READ, "hr", PolicyType.CLS, ["Salary"])]);
        static Dictionary<string, object> Row() => new() { ["_id"] = "1", ["Name"] = "Alice", ["Salary"] = 100 };

        var denied = new SchemaAccessRequest(User(roles: "staff"), schema, PolicyOperation.READ);
        var deniedProjection = Resolver.BuildReadProjection(denied, ["Name", "Salary"]);
        Assert.Single(deniedProjection.ClsReadPolicies);
        var maskedRow = Resolver.MaskRow(denied, Row(), deniedProjection);
        Assert.Equal("Alice", maskedRow["Name"]);
        Assert.Null(maskedRow["Salary"]);

        var allowed = new SchemaAccessRequest(User(roles: "hr"), schema, PolicyOperation.READ);
        var allowedRow = Resolver.MaskRow(allowed, Row(), Resolver.BuildReadProjection(allowed, ["Name", "Salary"]));
        Assert.Equal(100, allowedRow["Salary"]);
    }

    [Fact]
    public void MaskRow_RootTenant_LeavesRowUntouched()
    {
        var schema = Schema(
            read: SchemaAccessLevel.Custom,
            fields: [Field("Salary", "Int", read: SchemaAccessLevel.Custom)],
            policies: [RolePolicy(PolicyOperation.READ, "hr", PolicyType.CLS, ["Salary"])]);
        var request = new SchemaAccessRequest(User(root: true), schema, PolicyOperation.READ);
        var projection = Resolver.BuildReadProjection(request, ["Salary"]);
        Assert.Empty(projection.ClsReadPolicies);

        var row = new Dictionary<string, object> { ["_id"] = "1", ["Salary"] = 100 };
        Assert.Same(row, Resolver.MaskRow(request, row, projection));
        Assert.Equal(100, row["Salary"]);
    }

    [Fact]
    public void RestrictInput_UserLevelField_IsRemovedForAnonymousCaller()
    {
        var schema = Schema(write: SchemaAccessLevel.Public, fields: [Field("Name"), Field("Email", write: SchemaAccessLevel.User)]);
        var input = new Dictionary<string, object?> { ["Name"] = "a", ["Email"] = "e" };

        var restriction = Resolver.RestrictInput(new SchemaAccessRequest(User(authenticated: false), schema, PolicyOperation.WRITE), input);

        Assert.Equal(["Email"], restriction.RemovedFieldNames);
        Assert.True(input.ContainsKey("Name"));
        Assert.False(input.ContainsKey("Email"));
    }

    [Fact]
    public void RestrictInput_AuthenticatedCaller_KeepsUserLevelField()
    {
        var schema = Schema(write: SchemaAccessLevel.Public, fields: [Field("Name"), Field("Email", write: SchemaAccessLevel.User)]);
        var input = new Dictionary<string, object?> { ["Name"] = "a", ["Email"] = "e" };

        var restriction = Resolver.RestrictInput(new SchemaAccessRequest(User(), schema, PolicyOperation.WRITE), input);

        Assert.Empty(restriction.RemovedFieldNames);
        Assert.Equal(2, input.Count);
    }

    [Fact]
    public void ListAllowed_PublicSchema_AllOperations_CustomWithoutPolicies_None()
    {
        Assert.Equal(
            new[] { PolicyOperation.READ, PolicyOperation.WRITE, PolicyOperation.EDIT, PolicyOperation.DELETE },
            Resolver.ListAllowed(User(), Schema()).OrderBy(o => o));

        var locked = Schema(read: SchemaAccessLevel.Custom, write: SchemaAccessLevel.Custom, edit: SchemaAccessLevel.Custom, delete: SchemaAccessLevel.Custom);
        Assert.Empty(Resolver.ListAllowed(User(), locked));
    }

    [Theory]
    [InlineData("String", true)]
    [InlineData("Int", true)]
    [InlineData("Float", true)]
    [InlineData("Boolean", true)]
    [InlineData("DateTime", true)]
    [InlineData("ID", true)]
    [InlineData("GeoJson", true)]
    [InlineData("Enum", true)]
    [InlineData("Json", true)]
    [InlineData("Address", false)]
    public void IsScalar_KnowsTheStoredTypeVocabulary(string type, bool expected) => Assert.Equal(expected, SchemaTypeHelper.IsScalar(type));
}
