using System.Reflection;
using Blocks.Genesis.DataAccess.Rules;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// The allow-list of system schemas and properties open to validation rules and default values
/// (SYSTEM_SCHEMA_RULES_RISK.md). Paths must match the seeded SchemaDefinitions' field names.
/// </summary>
public class SystemSchemaRulesTests
{
    private static readonly string[] Allowed =
    [
        "User", "Role", "Permission", "Organization", "IdentityConfiguration", "IdentityProvider", "SignupLinkConfiguration",
        "BlocksClientConfig", "EmailTemplate", "MailServerConfiguration", "IntegrationSetup", "ResourceLimit",
        "WorkflowEntity", "FunctionEntity", "ProxyDetailEntity", "Schedule", "Repo", "MonitorConfiguration",
    ];

    [Fact]
    public void ExactlyTheEighteenAllowedSchemas_ResolveByClassName()
    {
        Assert.Equal(Allowed.Order(), SystemSchemaRules.SchemaNames.Order());
        Assert.All(Allowed, name => Assert.Equal(name, SystemSchemaRules.For(name)!.SchemaName));
    }

    [Fact]
    public void SystemClassNames_AreUnique()
    {
        var names = typeof(SystemSchemaRules).Assembly.GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("Blocks.Genesis.Entities.", StringComparison.Ordinal) == true)
            .GroupBy(t => t.Name)
            .Where(g => g.Count() > 1 && g.Key is var name && Allowed.Contains(name))
            .Select(g => g.Key);
        Assert.Empty(names);
    }

    [Theory]
    [InlineData("RefreshTokenModel")]
    [InlineData("Secret")]
    [InlineData("WorkflowExecutionEntity")]
    [InlineData("Build")]
    [InlineData("MonitorIncident")]
    [InlineData("MonitorPingLog")]
    [InlineData("NoSuchSchema")]
    public void ExcludedOrUnknownSchemas_HaveNoRules(string name)
    {
        Assert.Null(SystemSchemaRules.For(name));
    }

    [Fact]
    public void User_OpensProfileFields_WarnsOnSignInFields_BlocksSecurityState()
    {
        var user = SystemSchemaRules.For("User")!;

        Assert.Equal(RuleLevel.Warned, user.Level);
        Assert.Equal((RuleAccess.Allowed, RuleAccess.Allowed), (user.ValidationFor("FirstName"), user.DefaultFor("FirstName")));
        Assert.Equal((RuleAccess.Warned, RuleAccess.Blocked), (user.ValidationFor("Email"), user.DefaultFor("Email")));
        foreach (var blocked in new[] { "Password", "Roles", "LockoutCount", "MfaEnabled", "ItemId", "CreatedDate", "CreatedBy", "LastUpdatedDate", "LastUpdatedBy" })
        {
            Assert.Equal((RuleAccess.Blocked, RuleAccess.Blocked), (user.ValidationFor(blocked), user.DefaultFor(blocked)));
        }
    }

    [Fact]
    public void BaseEntityFields_AccessListsLanguageAndTags_UseTheSeedsDottedPaths()
    {
        var role = SystemSchemaRules.For("Role")!;

        foreach (var operation in new[] { "Create", "Read", "Update", "Delete" })
        foreach (var list in new[] { "Roles", "UserIds", "Permissions" })
        {
            Assert.Equal((RuleAccess.Warned, RuleAccess.Warned), (role.ValidationFor($"{operation}.{list}"), role.DefaultFor($"{operation}.{list}")));
        }
        Assert.Equal((RuleAccess.Allowed, RuleAccess.Allowed), (role.ValidationFor("Language"), role.DefaultFor("Language")));
        Assert.Equal((RuleAccess.Allowed, RuleAccess.Warned), (role.ValidationFor("Tags"), role.DefaultFor("Tags")));
        Assert.Equal(RuleAccess.Blocked, role.ValidationFor("OrganizationId"));
        Assert.DoesNotContain("Read", role.Fields.Keys);          // the object itself: rules sit on its lists
    }

    [Fact]
    public void MonitorConfiguration_OpensCheckSettings_WarnsOnHeadersAndPayload_BlocksStateAndLinks()
    {
        var monitor = SystemSchemaRules.For("MonitorConfiguration")!;

        Assert.Equal(RuleLevel.Normal, monitor.Level);
        foreach (var both in new[] { "ExpectedContent", "MonitorType", "ProtocolType", "HttpMethodType", "AuthorizationType", "MonitorSourceType" })
        {
            Assert.Equal((RuleAccess.Allowed, RuleAccess.Allowed), (monitor.ValidationFor(both), monitor.DefaultFor(both)));
        }
        foreach (var validationOnly in new[] { "Name", "Url", "IntervalInSeconds", "TimeoutInSeconds", "GracePeriodInSeconds", "SuccessHttpResponseCodes", "Regions", "Emails" })
        {
            Assert.Equal((RuleAccess.Allowed, RuleAccess.Blocked), (monitor.ValidationFor(validationOnly), monitor.DefaultFor(validationOnly)));
        }
        foreach (var warned in new[] { "CustomHttpHeaders", "CustomPayload" })
        {
            Assert.Equal((RuleAccess.Warned, RuleAccess.Blocked), (monitor.ValidationFor(warned), monitor.DefaultFor(warned)));
        }
        foreach (var blocked in new[] { "TenantId", "ExternalServiceId", "ExternalServiceName", "RepoId", "RepoName", "MonitorConfigurationType",
                     "MonitorSourcetypes", "LastIncidentAt", "LastCheckedAt", "IsActive", "CurrentStatus", "IncidentSummaries.StartTime", "ItemId", "OrganizationId" })
        {
            Assert.Equal((RuleAccess.Blocked, RuleAccess.Blocked), (monitor.ValidationFor(blocked), monitor.DefaultFor(blocked)));
        }
    }

    [Theory]
    [InlineData("Role", "Description", true)]           // string, never null on a typed entity
    [InlineData("ResourceLimit", "Limit", true)]        // int
    [InlineData("User", "FirstName", false)]            // string?
    [InlineData("Schedule", "StartDate", false)]        // DateTime?
    [InlineData("Role", "Read.Roles", false)]           // the parent object can be null
    [InlineData("MonitorConfiguration", "Name", true)]   // string, never null on a typed entity
    [InlineData("MonitorConfiguration", "MonitorType", false)] // nullable enum
    public void DefaultHasNoEffect_FollowsThePropertyType(string schema, string path, bool expected)
    {
        Assert.Equal(expected, SystemSchemaRules.For(schema)!.Fields[path].DefaultHasNoEffect);
    }

    [Fact]
    public void EveryAllowRulesAttribute_IsOnAPropertyOfAMarkedSchemaOrABaseClass()
    {
        // A typo or a removed property can't leave a stray mark: every marked property is a real property,
        // and it belongs to a system schema (or a base class one inherits from).
        var marked = typeof(SystemSchemaRules).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttribute<AllowRulesAttribute>() is not null)
                .Select(p => p.DeclaringType!))
            .Distinct()
            .ToList();

        Assert.All(marked, type => Assert.True(
            Allowed.Contains(type.Name) || typeof(SystemSchemaRules).Assembly.GetTypes().Any(t => Allowed.Contains(t.Name) && type.IsAssignableFrom(t)),
            $"{type.Name} has [AllowRules] but is not a system schema with rules"));
    }
}
