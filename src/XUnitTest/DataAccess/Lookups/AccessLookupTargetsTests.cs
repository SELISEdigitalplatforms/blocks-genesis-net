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
        return AccessLookupTargets.Build(employee, candidates).Find(rule.Reference!);
    }

    [Fact]
    public void Targets_EmployeeSelfTarget_Valid()
    {
        var rule = MyDepartment();
        var employee = Employee(Rls(PolicyOperation.READ, rule));

        // The policy schema is a candidate even when the caller doesn't pass it.
        var target = AccessLookupTargets.Build(employee, []).Find(rule.Reference!);

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

        Assert.NotNull(targets.Find(toTenant.Reference!)!.InvalidReason);
        Assert.Null(targets.Find(toRole.Reference!)!.InvalidReason);

        // A tenant schema sharing a system entity's name is never the target of a system-schema policy.
        var tenantRole = Department();
        tenantRole.SchemaName = "Role";
        var onlyTenantRole = AccessLookupTargets.Build(user, [tenantRole]);
        Assert.NotNull(onlyTenantRole.Find(toRole.Reference!)!.InvalidReason);
    }

    [Fact]
    public void Targets_FindByValue_DeepCopyFindsSameTarget_OtherValuesDont()
    {
        var employee = Employee(Rls(PolicyOperation.READ, MyDepartment()), Rls(PolicyOperation.READ, Lookup("DepartmentId", "Division", "ItemId")));
        var targets = AccessLookupTargets.Build(employee, []);
        var copy = RoundTrip(employee);
        var original = targets.Find(employee.Policies[0].RuleGroup.Rules[0].Reference!);

        Assert.NotSame(employee.Policies[0].RuleGroup.Rules[0].Reference, copy.Policies[0].RuleGroup.Rules[0].Reference);
        Assert.NotNull(original);
        Assert.Equal(original, targets.Find(copy.Policies[0].RuleGroup.Rules[0].Reference!));
        Assert.Equal(targets.Find(employee.Policies[1].RuleGroup.Rules[0].Reference!), targets.Find(copy.Policies[1].RuleGroup.Rules[0].Reference!));
        Assert.NotNull(targets.Find(copy.Policies[1].RuleGroup.Rules[0].Reference!)!.InvalidReason);

        // An equal-valued reference built separately matches too; a different filter, field or schema doesn't.
        Assert.Equal(original, targets.Find(MyDepartment().Reference!));
        Assert.Null(targets.Find(Lookup("DepartmentId", "Employee", "DepartmentId", where: WhereAuth("Name", "UserId")).Reference!));
        Assert.Null(targets.Find(Lookup("DepartmentId", "Employee", "Name", where: WhereAuth("UserId", "UserId")).Reference!));
        Assert.Null(targets.Find(Lookup("DepartmentId", "Department", "DepartmentId", where: WhereAuth("UserId", "UserId")).Reference!));
    }

    [Fact]
    public void Targets_EqualReferences_DifferentLeftFields_InvalidWins()
    {
        // Same reference value; Age (Int) can't be compared with DepartmentId (text), so neither rule is answered.
        var employee = Employee(Rls(PolicyOperation.READ, MyDepartment()),
            Rls(PolicyOperation.READ, Lookup("Age", "Employee", "DepartmentId", where: WhereAuth("UserId", "UserId"))));

        var targets = AccessLookupTargets.Build(employee, []);

        Assert.NotNull(targets.Find(MyDepartment().Reference!)!.InvalidReason);
    }
}
