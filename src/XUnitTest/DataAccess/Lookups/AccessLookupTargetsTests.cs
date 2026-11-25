#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.Entities.Data;
using static XUnitTest.DataAccess.Lookups.AccessLookupFixtures;

namespace XUnitTest.DataAccess.Lookups;

public sealed class AccessLookupTargetsTests
{
    private static AccessLookupTarget? TargetOf(PolicyRule rule, params Blocks.Genesis.DataAccess.Evaluation.SchemaDefinitionExtended[] candidates)
    {
        var employee = Employee(Rls(PolicyOperation.READ, rule));
        return AccessLookupTargets.Build(employee, candidates).Find(employee, rule);
    }

    [Fact]
    public void Targets_EmployeeSelfTarget_Valid()
    {
        var rule = MyDepartment();
        var employee = Employee(Rls(PolicyOperation.READ, rule));

        // The policy schema is a candidate even when the caller doesn't pass it.
        var target = AccessLookupTargets.Build(employee, []).Find(employee, rule);

        Assert.NotNull(target);
        Assert.Null(target.InvalidReason);
        Assert.Equal("Employee", target.SchemaName);
        Assert.Equal("DepartmentId", target.FieldName);
        Assert.Equal("Employees", target.CollectionName);
        Assert.Equal("DepartmentId", target.DbFieldName);
        Assert.False(target.IsSystemEntity);
        Assert.Equal(new[] { "Employee" }, AccessLookupTargets.TargetSchemaNames(employee));

        // ItemId is stored as _id.
        var byId = TargetOf(Lookup("DepartmentId", "Department", "ItemId", where: WhereAuth("MemberUserIds", "UserId")), Department());
        Assert.NotNull(byId);
        Assert.Null(byId.InvalidReason);
        Assert.Equal("_id", byId.DbFieldName);
        Assert.Equal("Departments", byId.CollectionName);
    }

    [Fact]
    public void Targets_DeletedOrRenamedField_Invalid()
    {
        var department = Department();
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Division", "ItemId"), department)!.InvalidReason);            // schema deleted
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "Title"), department)!.InvalidReason);           // field renamed
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "MemberUserIds"), department)!.InvalidReason);   // not a single value
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "Code"), department)!.InvalidReason);            // text vs number
        Assert.NotNull(TargetOf(Lookup("Missing", "Department", "ItemId"), department)!.InvalidReason);               // left field gone
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "ItemId", where: WhereAuth("Members", "UserId")), department)!.InvalidReason); // WHERE field renamed
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "ItemId", where: Lookup("Name", "Department", "Name")), department)!.InvalidReason); // lookup inside WHERE
        var unknownOperator = WhereStatic("Name", "HR");
        unknownOperator.Operator = (PolicyOperator)99;
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "ItemId", where: unknownOperator), department)!.InvalidReason); // operator outside the 15

        var deleted = Department();
        deleted.IsDeleted = true;
        Assert.NotNull(TargetOf(Lookup("DepartmentId", "Department", "ItemId"), deleted)!.InvalidReason);

        // Valid: number to number, nested WHERE on an existing field.
        Assert.Null(TargetOf(Lookup("Age", "Department", "Code"), department)!.InvalidReason);
        Assert.Null(TargetOf(Lookup("DepartmentId", "Department", "ItemId", where: WhereStatic("Name", "HR")), department)!.InvalidReason);
    }

    [Fact]
    public void Targets_SystemUser_Valid_SystemOrganization_InvalidUnverified()
    {
        var user = User();
        var organization = SystemSchema("Organization", "Name");

        var valid = TargetOf(Lookup("UserId", "User", "ItemId", where: WhereAuth("Email", "Email")), user, organization);
        Assert.NotNull(valid);
        Assert.Null(valid.InvalidReason);
        Assert.True(valid.IsSystemEntity);
        Assert.Equal("_id", valid.DbFieldName);
        Assert.Equal("Users", valid.CollectionName);

        // Password is not open to rules: neither as the target field nor in WHERE.
        Assert.NotNull(TargetOf(Lookup("UserId", "User", "Password"), user)!.InvalidReason);
        Assert.NotNull(TargetOf(Lookup("UserId", "User", "ItemId", where: WhereStatic("Password", "x")), user)!.InvalidReason);

        // Organization is open to rules but its collection isn't verified.
        Assert.NotNull(TargetOf(Lookup("UserId", "Organization", "ItemId", where: WhereStatic("Name", "Acme")), organization)!.InvalidReason);

        Assert.True(VerifiedSystemCollections.Contains("User"));
        Assert.True(VerifiedSystemCollections.Contains("Role"));
        Assert.False(VerifiedSystemCollections.Contains("Organization"));
        Assert.False(VerifiedSystemCollections.Contains("user"));
        Assert.False(VerifiedSystemCollections.Contains("Employee"));
    }

    [Fact]
    public void Targets_SystemPolicy_TenantTarget_Invalid()
    {
        var toTenant = Lookup("ItemId", "Employee", "UserId");
        var toRole = Lookup("ItemId", "Role", "ItemId");
        var user = User(Rls(PolicyOperation.READ, toTenant), Rls(PolicyOperation.READ, toRole));
        var targets = AccessLookupTargets.Build(user, [Employee(), SystemSchema("Role", "Name")]);

        Assert.NotNull(targets.Find(user, toTenant)!.InvalidReason);
        Assert.Null(targets.Find(user, toRole)!.InvalidReason);

        // A tenant schema sharing a system entity's name is never the target of a system-schema policy.
        var tenantRole = Department();
        tenantRole.SchemaName = "Role";
        var onlyTenantRole = AccessLookupTargets.Build(user, [tenantRole]);
        Assert.NotNull(onlyTenantRole.Find(user, toRole)!.InvalidReason);
    }

    [Fact]
    public void Targets_FindByValue_DeepCopyFindsSameTarget_OtherValuesDont()
    {
        var employee = Employee(Rls(PolicyOperation.READ, MyDepartment()), Rls(PolicyOperation.READ, Lookup("DepartmentId", "Division", "ItemId")));
        var targets = AccessLookupTargets.Build(employee, []);
        var copy = RoundTrip(employee);
        PolicyRule RuleOf(Blocks.Genesis.DataAccess.Evaluation.SchemaDefinitionExtended s, int i) => s.Policies[i].RuleGroup.Rules[0];
        var original = targets.Find(employee, RuleOf(employee, 0));

        Assert.NotSame(RuleOf(employee, 0).Reference, RuleOf(copy, 0).Reference);
        Assert.NotNull(original);
        Assert.Null(original.InvalidReason);
        Assert.Equal(original, targets.Find(copy, RuleOf(copy, 0)));
        Assert.Equal(targets.Find(employee, RuleOf(employee, 1)), targets.Find(copy, RuleOf(copy, 1)));
        Assert.NotNull(targets.Find(copy, RuleOf(copy, 1))!.InvalidReason);

        // An equal rule built separately matches too; a different filter, field, schema or left operand doesn't.
        Assert.Equal(original, targets.Find(employee, MyDepartment()));
        Assert.Null(targets.Find(employee, Lookup("DepartmentId", "Employee", "DepartmentId", where: WhereAuth("Name", "UserId"))));
        Assert.Null(targets.Find(employee, Lookup("DepartmentId", "Employee", "Name", where: WhereAuth("UserId", "UserId"))));
        Assert.Null(targets.Find(employee, Lookup("DepartmentId", "Department", "DepartmentId", where: WhereAuth("UserId", "UserId"))));
        Assert.Null(targets.Find(employee, Lookup("Name", "Employee", "DepartmentId", where: WhereAuth("UserId", "UserId"))));
    }

    [Fact]
    public void Targets_SameReference_DifferentlyTypedLeftFields_IndependentAnswers()
    {
        // Same reference; DepartmentId (text) fits the text target, Age (Int) doesn't.
        var text = MyDepartment();
        var number = Lookup("Age", "Employee", "DepartmentId", where: WhereAuth("UserId", "UserId"));
        var employee = Employee(Rls(PolicyOperation.READ, text), Rls(PolicyOperation.READ, number));

        var targets = AccessLookupTargets.Build(employee, []);

        Assert.Null(targets.Find(employee, text)!.InvalidReason);
        Assert.NotNull(targets.Find(employee, number)!.InvalidReason);
    }

    [Fact]
    public void Targets_LeftFieldRetypedInCopy_DoesNotReuseTarget()
    {
        var rule = MyDepartment();
        var employee = Employee(Rls(PolicyOperation.READ, rule));
        var targets = AccessLookupTargets.Build(employee, []);
        Assert.Null(targets.Find(employee, rule)!.InvalidReason);

        // A copy whose DepartmentId is now a number (or a list) doesn't find the target checked for text.
        var retyped = RoundTrip(employee);
        retyped.Fields = retyped.Fields.Select(f => f.Name == "DepartmentId" ? DataAccessTestSupport.Field("DepartmentId", "Int") : f).ToList();
        Assert.Null(targets.Find(retyped, retyped.Policies[0].RuleGroup.Rules[0]));

        var listed = RoundTrip(employee);
        listed.Fields = listed.Fields.Select(f => f.Name == "DepartmentId" ? DataAccessTestSupport.Field("DepartmentId", isArray: true) : f).ToList();
        Assert.Null(targets.Find(listed, listed.Policies[0].RuleGroup.Rules[0]));

        var removed = RoundTrip(employee);
        removed.Fields = removed.Fields.Where(f => f.Name != "DepartmentId").ToList();
        Assert.Null(targets.Find(removed, removed.Policies[0].RuleGroup.Rules[0]));
    }
}
