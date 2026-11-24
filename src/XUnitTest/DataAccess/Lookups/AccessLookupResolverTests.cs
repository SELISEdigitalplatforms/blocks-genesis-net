#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using static XUnitTest.DataAccess.Lookups.AccessLookupFixtures;

namespace XUnitTest.DataAccess.Lookups;

public sealed class AccessLookupResolverTests
{
    private static readonly PolicyOperation[] Read = [PolicyOperation.READ];

    private static AccessPrincipal Caller => DataAccessTestSupport.User(roles: "user");

    private static (AccessLookupResolver Resolver, RecordingRunner Runner, EventLogger<AccessLookupResolver> Log) Setup(RecordingRunner? runner = null)
    {
        runner ??= new RecordingRunner();
        var log = new EventLogger<AccessLookupResolver>();
        return (new AccessLookupResolver(runner, log), runner, log);
    }

    private static Task<SchemaDefinitionExtended> Resolve(AccessLookupResolver resolver, SchemaDefinitionExtended schema, AccessPrincipal? principal = null, PolicyOperation[]? operations = null, params SchemaDefinitionExtended[] candidates) =>
        resolver.ResolveAsync(schema, principal ?? Caller, operations ?? Read, AccessLookupTargets.Build(schema, candidates), CancellationToken.None);

    private static PolicyRule FirstRule(SchemaDefinitionExtended schema, int policy = 0) => schema.Policies[policy].RuleGroup.Rules[0];

    [Fact]
    public async Task Resolve_NoLookups_ReturnsSameInstance_ZeroCalls()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("X"));

        var plain = Employee(RoleRead("user"));
        Assert.Same(plain, await Resolve(resolver, plain));

        // A lookup only on WRITE: nothing to resolve for READ.
        var writeOnly = Employee(RoleRead("user"), Rls(PolicyOperation.WRITE, MyDepartment()));
        Assert.Same(writeOnly, await Resolve(resolver, writeOnly));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Resolve_EmployeeCase_ResolvesValues()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("X"));
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()));

        var resolved = await Resolve(resolver, schema);

        Assert.NotSame(schema, resolved);
        var rule = FirstRule(resolved);
        Assert.NotNull(rule.Resolution);
        Assert.Equal(LookupOutcome.Values, rule.Resolution.Outcome);
        Assert.Equal(new BsonArray { "X" }, rule.Resolution.Values);
        Assert.Null(FirstRule(schema).Resolution); // cached schema untouched

        var query = Assert.Single(runner.Calls);
        Assert.Equal("Employees", query.CollectionName);
        Assert.Equal("DepartmentId", query.DbFieldName);
        Assert.False(query.IsSystemEntity);
        Assert.Equal(5001, query.Limit);
    }

    [Fact]
    public async Task Resolve_SelfTarget_DoesNotApplyPoliciesToLookup()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("X"));
        // A second policy on Employee that would hide rows if it were applied to the lookup.
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()),
            Rls(PolicyOperation.READ, DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Name", PolicyOperator.EQUAL, staticValue: "only-me")));

        await Resolve(resolver, schema);

        var where = DataAccessPolicyHelper.BuildAccessLookupFilter(MyDepartment().Reference!.Filter, Caller).Match;
        var expected = new BsonDocument("$and", new BsonArray
        {
            where,
            new BsonDocument("DepartmentId", new BsonDocument("$ne", BsonNull.Value)),
            new BsonDocument("IsDeleted", new BsonDocument("$ne", true)),
        });
        Assert.Equal(expected, Assert.Single(runner.Calls).Match);
    }

    [Fact]
    public async Task Resolve_MissingAuth_OtherPolicyStillGrants()
    {
        var (resolver, runner, log) = Setup(new RecordingRunner("X"));
        var publicPolicy = RoleRead("user");
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()), publicPolicy);
        var noUserId = Caller with { UserId = null };

        var resolved = await Resolve(resolver, schema, noUserId);

        Assert.Empty(runner.Calls);
        Assert.Same(LookupResolution.Never, FirstRule(resolved).Resolution);
        Assert.Same(publicPolicy, resolved.Policies[1]);
        var entry = Assert.Single(log.Named("AccessLookupAuthMissing"));
        Assert.Contains("Employee", entry.Message, StringComparison.Ordinal);

        var result = resolved.Policies.EvaluatePolicies(noUserId, PolicyOperation.READ, PolicyType.RLS);
        Assert.True(result.IsAccessGranted);
        Assert.False(result.RequiresDataFilter);
    }

    [Fact]
    public async Task Resolve_IdenticalLookups_OneCall()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("X"));
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()), Rls(PolicyOperation.ALL, MyDepartment(PolicyOperator.NOT_EXISTS_IN)));

        var resolved = await Resolve(resolver, schema);

        Assert.Single(runner.Calls);
        Assert.Equal(LookupOutcome.Values, FirstRule(resolved, 0).Resolution!.Outcome);
        Assert.Equal(LookupOutcome.Values, FirstRule(resolved, 1).Resolution!.Outcome);
    }

    [Fact]
    public async Task Resolve_5001Values_Never()
    {
        var values = Enumerable.Range(0, 5001).Select(i => (BsonValue)("D" + i)).ToArray();
        var (resolver, runner, log) = Setup(new RecordingRunner(values));
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()), Rls(PolicyOperation.READ, MyDepartment(PolicyOperator.NOT_EXISTS_IN)));

        var resolved = await Resolve(resolver, schema);

        Assert.Single(runner.Calls);
        Assert.Same(LookupResolution.Never, FirstRule(resolved, 0).Resolution);
        Assert.Same(LookupResolution.Never, FirstRule(resolved, 1).Resolution);
        var entry = log.Named("AccessLookupTooLarge")[0];
        Assert.Contains("more than 5000 values", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("5001", entry.Message, StringComparison.Ordinal); // the query stops there: not a real count
        Assert.DoesNotContain("user-1", log.AllText(), StringComparison.Ordinal); // Auth values are never logged

        // Exactly 5,000 is still answered.
        var (resolver5000, _, _) = Setup(new RecordingRunner(values[..5000]));
        var ok = await Resolve(resolver5000, Employee(Rls(PolicyOperation.READ, MyDepartment())));
        Assert.Equal(5000, FirstRule(ok).Resolution!.Values.Count);
    }

    [Fact]
    public async Task Resolve_InvalidTarget_Never_NoCall()
    {
        var (resolver, runner, log) = Setup(new RecordingRunner("X"));
        var schema = Employee(
            Rls(PolicyOperation.READ, Lookup("DepartmentId", "Division", "ItemId")),
            Rls(PolicyOperation.READ, Lookup("DepartmentId", "Employee", "DepartmentId", PolicyOperator.NOT_EXISTS_IN, Lookup("UserId", "Employee", "UserId"))),
            Rls(PolicyOperation.READ, new PolicyRule { LeftOperand = "DepartmentId", Operator = PolicyOperator.NOT_EXISTS_IN }));

        var resolved = await Resolve(resolver, schema);

        Assert.Empty(runner.Calls);
        Assert.All(resolved.Policies, p => Assert.Same(LookupResolution.Never, p.RuleGroup.Rules[0].Resolution));
        Assert.Equal(3, log.Named("AccessLookupTargetMissing").Count);
        Assert.Contains("Division", log.AllText(), StringComparison.Ordinal);

        // A reference the targets don't know (not among the schema's rules) fails closed too.
        var other = await resolver.ResolveAsync(Employee(Rls(PolicyOperation.READ, MyDepartment())), Caller, Read, AccessLookupTargets.Build(schema, []), CancellationToken.None);
        Assert.Same(LookupResolution.Never, FirstRule(other).Resolution);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Resolve_BsonRoundTrippedCopy_UsesTargetsBuiltFromOriginal()
    {
        var (resolver, runner, log) = Setup(new RecordingRunner("X"));
        var original = Employee(Rls(PolicyOperation.READ, MyDepartment()),
            Rls(PolicyOperation.READ, Lookup("UserId", "User", "ItemId", where: WhereAuth("Email", "Email"))));
        var targets = AccessLookupTargets.Build(original, [User()]);
        var copy = RoundTrip(original);

        var resolved = await resolver.ResolveAsync(copy, Caller, Read, targets, CancellationToken.None);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(new BsonArray { "X" }, FirstRule(resolved, 0).Resolution!.Values);
        Assert.Equal(LookupOutcome.Values, FirstRule(resolved, 1).Resolution!.Outcome);
        Assert.Contains(runner.Calls, q => q.IsSystemEntity && q.CollectionName == "Users" && q.DbFieldName == "_id");
        Assert.Empty(log.Named("AccessLookupTargetMissing"));
        Assert.Null(FirstRule(copy).Resolution);
    }

    [Fact]
    public async Task Resolve_DenyPolicy_LeftUnresolved_NoQuery_RequestRefused()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("X"));
        var deny = Rls(PolicyOperation.READ, Lookup("DepartmentId", "Department", "ItemId", where: WhereAuth("MemberUserIds", "UserId")));
        deny.IsAllowPolicy = false;
        var allow = Rls(PolicyOperation.READ, MyDepartment());
        var schema = Employee(allow, deny);

        var resolved = await Resolve(resolver, schema, candidates: Department());

        // Only the allow policy's lookup ran; the deny policy is the same instance, its rule unresolved.
        var query = Assert.Single(runner.Calls);
        Assert.Equal("Employees", query.CollectionName);
        Assert.Equal(LookupOutcome.Values, FirstRule(resolved, 0).Resolution!.Outcome);
        Assert.Same(deny, resolved.Policies[1]);
        Assert.Null(FirstRule(resolved, 1).Resolution);
        Assert.Throws<Blocks.Genesis.AccessCheckFailedException>(() =>
            resolved.Policies.EvaluatePolicies(Caller, PolicyOperation.READ, PolicyType.RLS));

        // Missing Auth in the deny's WHERE must not lift the deny either: still refused, still no query.
        var (resolver2, runner2, _) = Setup(new RecordingRunner("X"));
        var denyOnly = Employee(RoleRead("user"), deny);
        var unresolved = await Resolve(resolver2, denyOnly, Caller with { UserId = null }, candidates: Department());
        Assert.Same(denyOnly, unresolved);
        Assert.Empty(runner2.Calls);
        Assert.Throws<Blocks.Genesis.AccessCheckFailedException>(() =>
            unresolved.Policies.EvaluatePolicies(Caller with { UserId = null }, PolicyOperation.READ, PolicyType.RLS));
    }

    [Fact]
    public async Task Resolve_NestedGroup_CopiesPathToLookupOnly()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("X"));
        var role = DataAccessTestSupport.Rule(ConditionSource.AUTH, "Roles", PolicyOperator.CONTAIN, staticValue: "user");
        var lookup = MyDepartment();
        var plainNested = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Name", PolicyOperator.EQUAL, staticValue: "n"));
        var lookupNested = new PolicyRuleGroup
        {
            LogicalOperator = PolicyLogicalOperator.OR,
            Rules = [],
            NestedGroups = [DataAccessTestSupport.Group(PolicyLogicalOperator.AND, lookup)],
        };
        var root = new PolicyRuleGroup { LogicalOperator = PolicyLogicalOperator.AND, Rules = [role], NestedGroups = [plainNested, lookupNested] };
        var policy = DataAccessTestSupport.Policy(PolicyType.RLS, PolicyOperation.READ, [], root);
        var schema = Employee(policy);

        var resolved = await Resolve(resolver, schema);

        Assert.Single(runner.Calls);
        var copiedRoot = resolved.Policies[0].RuleGroup;
        Assert.NotSame(root, copiedRoot);
        Assert.Same(role, copiedRoot.Rules[0]);
        Assert.Same(plainNested, copiedRoot.NestedGroups[0]);
        Assert.NotSame(lookupNested, copiedRoot.NestedGroups[1]);
        var copiedInner = copiedRoot.NestedGroups[1].NestedGroups[0];
        Assert.NotSame(lookupNested.NestedGroups[0], copiedInner);
        Assert.NotSame(lookup, copiedInner.Rules[0]);
        Assert.Equal(new BsonArray { "X" }, copiedInner.Rules[0].Resolution!.Values);
        Assert.Null(lookup.Resolution);
        Assert.Equal(PolicyLogicalOperator.OR, copiedRoot.NestedGroups[1].LogicalOperator);

        var result = resolved.Policies.EvaluatePolicies(Caller, PolicyOperation.READ, PolicyType.RLS);
        Assert.True(result.IsAccessGranted);
        Assert.True(result.RequiresDataFilter);
    }

    [Fact]
    public async Task Resolve_SystemPolicy_TenantTarget_Never_NoQuery()
    {
        var (resolver, runner, log) = Setup(new RecordingRunner("X"));
        var schema = User(Rls(PolicyOperation.READ, Lookup("ItemId", "Employee", "UserId")));

        var resolved = await Resolve(resolver, schema, candidates: Employee());

        Assert.Empty(runner.Calls);
        Assert.Same(LookupResolution.Never, FirstRule(resolved).Resolution);
        Assert.Single(log.Named("AccessLookupTargetMissing"));
    }

    [Fact]
    public async Task Resolve_RunnerReturnsNull_Throws()
    {
        var resolver = new AccessLookupResolver(new NullRunner(), new EventLogger<AccessLookupResolver>());
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(schema, Caller, Read, AccessLookupTargets.Build(schema, []), CancellationToken.None));
        Assert.Equal("Access lookup runner returned no result.", ex.Message);
    }

    private sealed class NullRunner : IAccessLookupRunner
    {
        public Task<IReadOnlyList<BsonValue>> RunAsync(AccessLookupQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BsonValue>>(null!);
    }

    [Fact]
    public async Task Resolve_Empty_ExistsNever_NotExistsAlways()
    {
        // Nulls are dropped: {$in: [null]} would match rows without the field.
        var (resolver, runner, _) = Setup(new RecordingRunner(BsonNull.Value));
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()), Rls(PolicyOperation.READ, MyDepartment(PolicyOperator.NOT_EXISTS_IN)));

        var resolved = await Resolve(resolver, schema);

        Assert.Single(runner.Calls);
        Assert.Same(LookupResolution.Never, FirstRule(resolved, 0).Resolution);
        Assert.Same(LookupResolution.Always, FirstRule(resolved, 1).Resolution);

        var (resolver2, _, _) = Setup(new RecordingRunner("X", BsonNull.Value));
        var mixed = await Resolve(resolver2, Employee(Rls(PolicyOperation.READ, MyDepartment())));
        Assert.Equal(new BsonArray { "X" }, FirstRule(mixed).Resolution!.Values);
    }

    [Fact]
    public async Task Resolve_CopiesOnlyLookupPoliciesAndTheirClsCopies()
    {
        var (resolver, _, _) = Setup(new RecordingRunner("X"));
        var lookupPolicy = Rls(PolicyOperation.READ, MyDepartment());
        var clsCopy = lookupPolicy.Clone();
        clsCopy.PolicyType = PolicyType.CLS;
        clsCopy.FieldNames = ["Name"];
        clsCopy.ItemId = "cls-copy";
        var other = RoleRead("user");
        var otherCls = other.Clone();
        otherCls.PolicyType = PolicyType.CLS;
        var schema = Employee(lookupPolicy, other, clsCopy, otherCls);
        var originalPolicies = schema.Policies.ToList();
        var originalGroup = lookupPolicy.RuleGroup;
        var originalRule = originalGroup.Rules[0];

        var resolved = await Resolve(resolver, schema);

        // Input unchanged.
        Assert.Equal(originalPolicies, schema.Policies);
        Assert.Same(originalGroup, lookupPolicy.RuleGroup);
        Assert.Same(originalGroup, clsCopy.RuleGroup);
        Assert.Null(originalRule.Resolution);

        // Policies without lookups are the same instances.
        Assert.Same(other, resolved.Policies[1]);
        Assert.Same(otherCls, resolved.Policies[3]);

        // The lookup policy and its column copy are shallow copies sharing one copied rule group.
        var policyCopy = resolved.Policies[0];
        var clsCopyCopy = resolved.Policies[2];
        Assert.NotSame(lookupPolicy, policyCopy);
        Assert.NotSame(clsCopy, clsCopyCopy);
        Assert.Equal(lookupPolicy.ItemId, policyCopy.ItemId);
        Assert.Equal("cls-copy", clsCopyCopy.ItemId);
        Assert.Equal(PolicyType.CLS, clsCopyCopy.PolicyType);
        Assert.Equal(new[] { "Name" }, clsCopyCopy.FieldNames);
        Assert.NotSame(originalGroup, policyCopy.RuleGroup);
        Assert.Same(policyCopy.RuleGroup, clsCopyCopy.RuleGroup);
        Assert.Equal(LookupOutcome.Values, policyCopy.RuleGroup.Rules[0].Resolution!.Outcome);

        // The schema copy keeps everything else.
        Assert.Same(schema.Fields, resolved.Fields);
        Assert.Equal(schema.SchemaName, resolved.SchemaName);
        Assert.Equal(schema.ItemId, resolved.ItemId);
        Assert.Equal(schema.CollectionName, resolved.CollectionName);
    }

    [Fact]
    public async Task Resolve_SystemUserTarget_RunsOnSystemCollection()
    {
        var (resolver, runner, _) = Setup(new RecordingRunner("user-1"));
        var schema = Employee(Rls(PolicyOperation.READ, Lookup("UserId", "User", "ItemId", where: WhereAuth("Email", "Email"))));

        var resolved = await Resolve(resolver, schema, candidates: User());

        var query = Assert.Single(runner.Calls);
        Assert.True(query.IsSystemEntity);
        Assert.Equal("Users", query.CollectionName);
        Assert.Equal("_id", query.DbFieldName);
        Assert.Equal(LookupOutcome.Values, FirstRule(resolved).Resolution!.Outcome);
    }

    [Fact]
    public async Task EndToEnd_EmployeeDepartment_FilterAndRows()
    {
        var (resolver, _, _) = Setup(new RecordingRunner("X"));
        var schema = Employee(Rls(PolicyOperation.READ, MyDepartment()));

        var resolved = await Resolve(resolver, schema);

        var result = resolved.Policies.EvaluatePolicies(Caller, PolicyOperation.READ, PolicyType.RLS);
        Assert.True(result.IsAccessGranted);
        Assert.True(result.RequiresDataFilter);

        var rows = new[]
        {
            new BsonDocument { { "_id", "e1" }, { "DepartmentId", "X" } },
            new BsonDocument { { "_id", "e2" }, { "DepartmentId", "Y" } },
            new BsonDocument { { "_id", "e3" } },
        };
        var byFilter = rows.Where(r => BsonFilterMatcher.Matches(r, result.DataFilter)).Select(r => r["_id"].AsString).ToList();
        var policy = resolved.Policies[0];
        var inMemory = rows
            .Where(r => QueryPolicyRuleEvaluator.RowSatisfiesPolicy(Caller, policy, r.ToDictionary()))
            .Select(r => r["_id"].AsString).ToList();

        Assert.Equal(new[] { "e1" }, byFilter);
        Assert.Equal(byFilter, inMemory);

        // The cached schema still refuses: its rule was never resolved.
        Assert.Throws<Blocks.Genesis.AccessCheckFailedException>(() =>
            schema.Policies.EvaluatePolicies(Caller, PolicyOperation.READ, PolicyType.RLS));
    }
}
