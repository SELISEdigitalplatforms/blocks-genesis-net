// Test names use underscores and tests await without ConfigureAwait, as everywhere in this project (xUnit1030).
#pragma warning disable CA1707, CA2007
using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// G3 (row rule on written rows), G4 (column rules refuse instead of strip, data rules read the stored row on EDIT)
/// and G5 (policy denials carry FORBIDDEN), on the resolution service.
/// </summary>
public class WritePolicyTests
{
    private static readonly DataAccessPolicyResolutionService Resolver = new(new SchemaPolicySource());

    private static DataAccessPolicy OrgPolicy(PolicyOperation operation) =>
        Policy(PolicyType.RLS, operation, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "OrganizationId", PolicyOperator.EQUAL, ConditionSource.AUTH, "organizationId")));

    private static SchemaDefinitionExtended OrgSchema(PolicyOperation operation, params DataAccessPolicy[] extra) =>
        Schema(write: SchemaAccessLevel.Custom, edit: SchemaAccessLevel.Custom,
            fields: [Field("OrganizationId"), Field("Name"), Field("Salary", "Float"), Field("CreatedBy")],
            policies: [OrgPolicy(operation), .. extra]);

    private static PolicyEvaluationResult CheckRow(SchemaDefinitionExtended schema, PolicyOperation operation, BsonDocument row, AccessPrincipal? principal = null)
    {
        var request = new SchemaAccessRequest(principal ?? User(), schema, operation);
        return Resolver.CheckRow(request, Resolver.Check(request), row);
    }

    [Fact]
    public void Insert_RowInsideWriteFilter_Granted_OutsideRefusedWithForbidden()
    {
        var schema = OrgSchema(PolicyOperation.WRITE);

        Assert.True(CheckRow(schema, PolicyOperation.WRITE, new BsonDocument { { "OrganizationId", "org-1" }, { "Name", "a" } }).IsAccessGranted);

        var outside = CheckRow(schema, PolicyOperation.WRITE, new BsonDocument { { "OrganizationId", "org-2" }, { "Name", "a" } });
        Assert.False(outside.IsAccessGranted);
        Assert.Equal(PolicyErrorCodes.Forbidden, outside.ErrorCode);
        Assert.Equal(PolicyErrorCodes.NoPolicyGrantsAccess, outside.ErrorMessage);

        Assert.False(CheckRow(schema, PolicyOperation.WRITE, new BsonDocument("Name", "no org")).IsAccessGranted);
    }

    [Fact]
    public void Update_ResultingRowMovedOutOfEditFilter_Refused()
    {
        var schema = OrgSchema(PolicyOperation.EDIT);
        var stored = new BsonDocument { { "OrganizationId", "org-1" }, { "Name", "a" } };
        var update = RowChanges.ApplyUpdate(stored, new BsonDocument("$set", new BsonDocument("OrganizationId", "org-2")));

        Assert.True(CheckRow(schema, PolicyOperation.EDIT, stored).IsAccessGranted);
        Assert.False(CheckRow(schema, PolicyOperation.EDIT, update.Row).IsAccessGranted);
    }

    [Fact]
    public void RowRule_NotChecked_ForNonCustomLevel_RootTenant_AndTokenOnlyPolicy()
    {
        var outside = new BsonDocument("OrganizationId", "org-2");
        var publicSchema = Schema(write: SchemaAccessLevel.Public, policies: [OrgPolicy(PolicyOperation.WRITE)]);
        Assert.True(CheckRow(publicSchema, PolicyOperation.WRITE, outside).IsAccessGranted);

        Assert.True(CheckRow(OrgSchema(PolicyOperation.WRITE), PolicyOperation.WRITE, outside, User(root: true)).IsAccessGranted);

        var roleOnly = Schema(write: SchemaAccessLevel.Custom, policies: [RolePolicy(PolicyOperation.WRITE, "admin")]);
        Assert.True(CheckRow(roleOnly, PolicyOperation.WRITE, outside, User(roles: "admin")).IsAccessGranted);
    }

    [Fact]
    public void RowRule_NoPolicy_DeniedWithForbidden()
    {
        var schema = Schema(write: SchemaAccessLevel.Custom);
        var result = CheckRow(schema, PolicyOperation.WRITE, new BsonDocument("Name", "a"));

        Assert.False(result.IsAccessGranted);
        Assert.Equal(PolicyErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(PolicyErrorCodes.NoPolicyGrantsAccess, result.ErrorMessage);
    }

    [Fact]
    public void RowRule_FilterThatCannotBeChecked_FailsClosed()
    {
        var schema = OrgSchema(PolicyOperation.WRITE);
        var request = new SchemaAccessRequest(User(), schema, PolicyOperation.WRITE);
        var rls = new PolicyEvaluationResult
        {
            IsAccessGranted = true,
            RequiresDataFilter = true,
            DataFilter = new BsonDocument("Items", new BsonDocument("$elemMatch", new BsonDocument("A", 1)))
        };

        var result = Resolver.CheckRow(request, rls, new BsonDocument("Items", new BsonArray()));

        Assert.False(result.IsAccessGranted);
        Assert.Equal(PolicyErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public void Columns_RefusedFieldIsNamed_NotStripped()
    {
        var salaryAdminOnly = RolePolicy(PolicyOperation.EDIT, "hr", PolicyType.CLS, ["Salary"]);
        var schema = Schema(edit: SchemaAccessLevel.Custom,
            fields: [Field("Name", edit: SchemaAccessLevel.Public), Field("Salary", "Float", edit: SchemaAccessLevel.Custom)],
            policies: [salaryAdminOnly]);
        var request = new SchemaAccessRequest(User(roles: "staff"), schema, PolicyOperation.EDIT);

        var refused = Resolver.CheckInput(request, ["Name", "Salary"], dataRow: null);
        Assert.False(refused.IsAccessGranted);
        Assert.Equal(PolicyErrorCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(["Salary"], refused.ExcludedFields);
        Assert.Equal("No policy grants edit access to field 'Salary'.", refused.ErrorMessage);

        Assert.True(Resolver.CheckInput(request with { Principal = User(roles: "hr") }, ["Name", "Salary"], null).IsAccessGranted);
        Assert.True(Resolver.CheckInput(request with { Principal = User(root: true) }, ["Salary"], null).IsAccessGranted);
        Assert.True(Resolver.CheckInput(request, ["Name", "NotInSchema"], null).IsAccessGranted);
    }

    [Fact]
    public void Columns_DataRuleOnEdit_IsCheckedAgainstStoredRow()
    {
        // "Only the creator may change Salary": before, this passed for every writer because no row was read.
        var ownerOnly = Policy(PolicyType.CLS, PolicyOperation.EDIT, ["Salary"],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId")));
        var schema = Schema(edit: SchemaAccessLevel.Custom,
            fields: [Field("Salary", "Float", edit: SchemaAccessLevel.Custom), Field("CreatedBy")],
            policies: [ownerOnly]);
        var request = new SchemaAccessRequest(User(), schema, PolicyOperation.EDIT);

        Assert.True(Resolver.CheckInput(request, ["Salary"], new BsonDocument("CreatedBy", "user-1")).IsAccessGranted);
        var other = Resolver.CheckInput(request, ["Salary"], new BsonDocument("CreatedBy", "user-2"));
        Assert.False(other.IsAccessGranted);
        Assert.Equal(["Salary"], other.ExcludedFields);
        // Without a row the rule passes on its token part, as before.
        Assert.True(Resolver.CheckInput(request, ["Salary"], null).IsAccessGranted);
    }

    [Fact]
    public void Columns_UserLevelField_RefusedForAnonymous()
    {
        var schema = Schema(write: SchemaAccessLevel.Public, fields: [Field("Email", write: SchemaAccessLevel.User)]);
        var anonymous = AccessPrincipal.Anonymous;

        var result = Resolver.CheckInput(new SchemaAccessRequest(anonymous, schema, PolicyOperation.WRITE), ["Email"], new BsonDocument("Email", "x"));

        Assert.False(result.IsAccessGranted);
        Assert.Contains("write access to field 'Email'", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluatePolicies_EveryDenial_CarriesForbidden()
    {
        var principal = User(roles: "guest");
        Assert.Equal(PolicyErrorCodes.Forbidden, new List<DataAccessPolicy>().EvaluatePolicies(principal, PolicyOperation.READ, PolicyType.RLS).ErrorCode);
        Assert.Equal(PolicyErrorCodes.Forbidden, new List<DataAccessPolicy> { RolePolicy(PolicyOperation.READ, "admin") }
            .EvaluatePolicies(principal, PolicyOperation.READ, PolicyType.RLS).ErrorCode);

        var deny = RolePolicy(PolicyOperation.READ, "guest");
        deny.IsAllowPolicy = false;
        var denied = new List<DataAccessPolicy> { deny }.EvaluatePolicies(principal, PolicyOperation.READ, PolicyType.RLS);
        Assert.Equal(PolicyErrorCodes.Forbidden, denied.ErrorCode);

        var granted = new List<DataAccessPolicy> { RolePolicy(PolicyOperation.READ, "guest") }.EvaluatePolicies(principal, PolicyOperation.READ, PolicyType.RLS);
        Assert.True(granted.IsAccessGranted);
        Assert.Null(granted.ErrorCode);
    }

    [Fact]
    public void Check_CustomWithoutPolicies_DeniedWithForbidden()
    {
        var result = Resolver.Check(new SchemaAccessRequest(User(), Schema(edit: SchemaAccessLevel.Custom), PolicyOperation.EDIT));

        Assert.False(result.IsAccessGranted);
        Assert.Equal(PolicyErrorCodes.Forbidden, result.ErrorCode);
        var ex = Assert.Throws<BlocksForbiddenException>(result.ThrowIfDenied);
        Assert.Equal("FORBIDDEN", ex.Code);
        Assert.Equal(PolicyErrorCodes.NoPolicyGrantsAccess, ex.Message);
    }
}

public class RowChangesTests
{
    [Fact]
    public void ApplyUpdate_SetUnsetIncCurrentDate_AndChangedPathsWithParents()
    {
        var stored = new BsonDocument { { "Name", "a" }, { "Count", 1 }, { "Contact", new BsonDocument("Email", "x") }, { "Old", 1 } };
        var update = new BsonDocument
        {
            { "$set", new BsonDocument("Contact.Email", "y") },
            { "$unset", new BsonDocument("Old", "") },
            { "$inc", new BsonDocument("Count", 2) },
            { "$currentDate", new BsonDocument("Touched", true) },
            { "$setOnInsert", new BsonDocument("Created", 1) },
        };

        var result = RowChanges.ApplyUpdate(stored, update);

        Assert.Equal("y", result.Row["Contact"]["Email"].AsString);
        Assert.False(result.Row.Contains("Old"));
        Assert.Equal(3, result.Row["Count"].ToInt32());
        Assert.True(result.Row.Contains("Touched"));
        Assert.False(result.Row.Contains("Created"));
        Assert.Equal(["Contact", "Contact.Email", "Old", "Count", "Touched"], result.ChangedPaths);
        Assert.Empty(result.UncheckablePaths);
        Assert.Equal("x", stored["Contact"]["Email"].AsString); // stored row untouched
    }

    [Fact]
    public void ApplyUpdate_OtherOperatorsAndPositionalPaths_AreUncheckable()
    {
        var stored = new BsonDocument { { "Tags", new BsonArray { "a" } }, { "Items", new BsonArray { new BsonDocument("N", 1) } } };
        var update = new BsonDocument
        {
            { "$push", new BsonDocument("Tags", "b") },
            { "$set", new BsonDocument("Items.$.N", 2) },
        };

        var result = RowChanges.ApplyUpdate(stored, update);

        Assert.Equal(["Tags", "Items.$.N"], result.UncheckablePaths);
    }

    [Fact]
    public void ApplyUpdate_Replacement_IsTheNewRow()
    {
        var stored = new BsonDocument { { "Name", "a" }, { "Same", 1 } };
        var replacement = new BsonDocument { { "Name", "b" }, { "Same", 1 }, { "Added", new BsonArray { 1 } } };

        var result = RowChanges.ApplyUpdate(stored, replacement);

        Assert.Same(replacement, result.Row);
        Assert.Equal(["Name", "Added", "Added.0"], result.ChangedPaths);
    }

    [Fact]
    public void AllPaths_ListsRootNestedAndItems()
    {
        var row = new BsonDocument { { "A", 1 }, { "B", new BsonDocument("C", 2) }, { "L", new BsonArray { new BsonDocument("D", 3) } } };

        Assert.Equal(["A", "B", "B.C", "L", "L.0", "L.0.D"], RowChanges.AllPaths(row));
    }
}
