using Blocks.Genesis.Entities.Logic;
using Blocks.Genesis.Entities.Os;
using Blocks.Genesis.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace XUnitTest.Notifications;

// Against the local test Mongo: the organization, active-user and permission rules live in the queries.
[Trait("Category", "Integration")]
public sealed class NotificationReceiverResolverTests : IAsyncLifetime
{
    private const string Org = NotificationMongo.Org;
    private const string OtherOrg = NotificationMongo.OtherOrg;
    private const string UsersPermission = "blocks-iam::iam::users";
    private readonly NotificationMongo _mongo = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _mongo.DisposeAsync();

    private Task<NotificationReceivers> ResolveAsync(QueuedNotification item, NotificationConfiguration configuration) =>
        new NotificationReceiverResolver(_mongo.Directory(), NullLogger<NotificationReceiverResolver>.Instance)
            .ResolveAsync(item, configuration, CancellationToken.None);

    private static NotificationAudience Level(NotificationAudienceLevel level) => new() { Level = level };

    [Fact]
    public async Task AffectedOnly_NamedUserRemovedFromTheOrganization_StillReceives()
    {
        // e.g. "roles revoked": the user is no longer in the send's organization.
        await _mongo.AddUserAsync("u-in", [Org]);
        await _mongo.AddUserAsync("u-other-org", [OtherOrg]);
        await _mongo.AddUserAsync("u-no-org", []);

        var result = await ResolveAsync(Sample.Item(Org, "u-in", "u-other-org", "u-no-org"), Sample.Config());

        Assert.Equal(["u-in", "u-no-org", "u-other-org"], result.ProjectUserIds.Order());
        Assert.Null(result.OrganizationWideId);
    }

    [Fact]
    public async Task AffectedOnly_InactiveOrUnknownNamedUser_NeverReceives()
    {
        await _mongo.AddUserAsync("u-in", [Org]);
        await _mongo.AddUserAsync("u-inactive", [Org], active: false);

        var result = await ResolveAsync(Sample.Item(Org, "u-in", "u-inactive", "u-unknown"), Sample.Config());

        Assert.Equal(["u-in"], result.ProjectUserIds);
    }

    [Fact]
    public async Task AffectedOnly_NamedUserOfAnotherTenant_NeverReceives()
    {
        // The root DB stands in for another tenant's DB: the same id there must not count for this tenant.
        await _mongo.AddUserAsync("u-elsewhere", [Org], database: _mongo.RootDb);

        var result = await ResolveAsync(Sample.Item(Org, "u-elsewhere"), Sample.Config());

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task AffectedOnly_NoOrganization_NamedUsersStillReceive()
    {
        await _mongo.AddUserAsync("u1", [OtherOrg]);

        var result = await ResolveAsync(Sample.Item(null, "u1"), Sample.Config());

        Assert.Equal(["u1"], result.ProjectUserIds);
    }

    [Fact]
    public async Task AllUsers_IsOneOrganizationWideSend_WithoutPush()
    {
        await _mongo.AddUserAsync("u1", [Org]);

        var result = await ResolveAsync(Sample.Item(Org), Sample.Config(Level(NotificationAudienceLevel.AllUsers)));

        Assert.Equal(Org, result.OrganizationWideId);
        Assert.Empty(result.ProjectUserIds);
    }

    [Fact]
    public async Task AllUsers_WithPush_ListsActiveMembersOfTheOrganizationOnly()
    {
        await _mongo.AddUserAsync("u1", [Org]);
        await _mongo.AddUserAsync("u2", [OtherOrg]);
        await _mongo.AddUserAsync("u3", [Org], active: false);
        var configuration = Sample.Config(Level(NotificationAudienceLevel.AllUsers));
        configuration.PushEnabled = true;

        var result = await ResolveAsync(Sample.Item(Org), configuration);

        Assert.Equal(Org, result.OrganizationWideId);
        Assert.Equal(["u1"], result.ProjectUserIds);
    }

    [Theory]
    [InlineData(NotificationAudienceLevel.AllUsers)]
    [InlineData(NotificationAudienceLevel.Selected)]
    public async Task OrganizationLevels_WithoutOrganization_ReachNobody(NotificationAudienceLevel level)
    {
        await _mongo.AddUserAsync("u1", [Org]);
        var audience = new NotificationAudience { Level = level, UserIds = ["u1"] };

        var result = await ResolveAsync(Sample.Item(null, "u1"), Sample.Config(audience));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task Selected_Any_RoleOrDirectPermissionInTheOrganization()
    {
        await _mongo.AddUserAsync("by-role", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("by-permission", [Org], permissions: new() { [Org] = [UsersPermission] });
        await _mongo.AddUserAsync("role-elsewhere", [Org, OtherOrg], roles: new() { [OtherOrg] = ["admin"] });
        await _mongo.AddUserAsync("plain", [Org]);
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, Roles = ["admin"], Permissions = [UsersPermission] };

        var result = await ResolveAsync(Sample.Item(Org), Sample.Config(audience));

        Assert.Equal(["by-permission", "by-role"], result.ProjectUserIds.Order());
    }

    [Fact]
    public async Task Selected_All_NeedsEveryList()
    {
        await _mongo.AddUserAsync("both", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("role-only", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("listed-only", [Org]);
        var audience = new NotificationAudience
        {
            Level = NotificationAudienceLevel.Selected,
            Match = NotificationAudienceMatch.All,
            UserIds = ["both", "listed-only"],
            Roles = ["admin"],
        };

        var result = await ResolveAsync(Sample.Item(Org), Sample.Config(audience));

        Assert.Equal(["both"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Selected_PermissionGrantedToRole_CountsOnlyInTheSameOrganization()
    {
        await _mongo.AddPermissionAsync(UsersPermission, Org, "manager");
        await _mongo.AddPermissionAsync(UsersPermission, OtherOrg, "viewer");
        await _mongo.AddUserAsync("manager", [Org], roles: new() { [Org] = ["manager"] });
        await _mongo.AddUserAsync("viewer", [Org], roles: new() { [Org] = ["viewer"] });
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, Permissions = [UsersPermission] };

        var result = await ResolveAsync(Sample.Item(Org), Sample.Config(audience));

        Assert.Equal(["manager"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Selected_AlsoNamedUsers_AddsNamedUsersOfAnyOrganization_RulesStayInTheOrganization()
    {
        await _mongo.AddUserAsync("admin", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("admin-elsewhere", [OtherOrg], roles: new() { [OtherOrg] = ["admin"] });
        await _mongo.AddUserAsync("affected", [Org]);
        await _mongo.AddUserAsync("affected-elsewhere", [OtherOrg]);
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, Roles = ["admin"], AlsoNamedUsers = true };

        var result = await ResolveAsync(Sample.Item(Org, "affected", "affected-elsewhere"), Sample.Config(audience));

        Assert.Equal(["admin", "affected", "affected-elsewhere"], result.ProjectUserIds.Order());
    }

    [Fact]
    public async Task Selected_AlsoNamedUsers_NoOrganization_LockedNamedUserReceives_RulesReachNobody()
    {
        // e.g. "account locked": iam sends without an organization, Selected (admins) + AlsoNamedUsers.
        await _mongo.AddUserAsync("admin", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("locked", [Org], lockoutUntilUtc: _mongo.Time.Now.UtcDateTime.AddMinutes(15));
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, Roles = ["admin"], AlsoNamedUsers = true };

        var result = await ResolveAsync(Sample.Item(null, "locked"), Sample.Config(audience));

        Assert.Equal(["locked"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Selected_AlsoDoneBy_AddsTheUserWhoDidTheAction_NotTheOwner()
    {
        await _mongo.AddUserAsync("doer", [OtherOrg]);
        await _mongo.AddUserAsync("owner", [Org]);
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, AlsoDoneBy = true };
        var item = Sample.Item(Org, "owner") with { Sender = new NotificationSender("doer", NotificationMongo.TenantId, false) };

        var result = await ResolveAsync(item, Sample.Config(audience));

        Assert.Equal(["doer"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Selected_OwnerAndDoneBy_SamePerson_ReceivesOnce()
    {
        await _mongo.AddUserAsync("sara", [Org]);
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, AlsoNamedUsers = true, AlsoDoneBy = true };
        var item = Sample.Item(Org, "sara") with { Sender = new NotificationSender("sara", NotificationMongo.TenantId, false) };

        var result = await ResolveAsync(item, Sample.Config(audience));

        Assert.Equal(["sara"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Selected_AlsoDoneBy_InactiveOrUnknownDoer_NeverReceives()
    {
        await _mongo.AddUserAsync("inactive", [Org], active: false);
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, AlsoDoneBy = true };

        var inactive = await ResolveAsync(Sample.Item(Org) with { Sender = new NotificationSender("inactive", "", false) }, Sample.Config(audience));
        var apiKey = await ResolveAsync(Sample.Item(Org) with { Sender = new NotificationSender("client-id", "", false) }, Sample.Config(audience));

        Assert.True(inactive.IsEmpty);
        Assert.True(apiKey.IsEmpty);
    }

    [Fact]
    public async Task Console_DoneByRootUserWithProjectAccess_Receives()
    {
        await _mongo.AddProjectPeopleAsync("root-doer", NotificationMongo.TenantId);
        var audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, AlsoDoneBy = true };
        var item = Sample.Item(Org) with { Sender = new NotificationSender("root-doer", NotificationMongo.TenantId, false) };

        var result = await ResolveAsync(item, Sample.Config(audience, NotificationScope.Console));

        Assert.Equal(["root-doer"], result.ConsoleUserIds);
    }

    [Fact]
    public async Task Filters_OnlyNarrow_NeverAddASubscriberOutsideTheAudience()
    {
        await _mongo.AddUserAsync("subscribed", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("not-subscribed", [Org], roles: new() { [Org] = ["admin"] });
        await _mongo.AddUserAsync("subscribed-outsider", [Org]);
        await _mongo.AddUserAsync("stale", [Org], roles: new() { [Org] = ["admin"] });
        var now = _mongo.Time.Now.UtcDateTime;
        await _mongo.AddSubscriptionAsync("subscribed", "orders", now.AddMinutes(-5));
        await _mongo.AddSubscriptionAsync("subscribed-outsider", "orders", now.AddMinutes(-5));
        await _mongo.AddSubscriptionAsync("stale", "orders", now.AddHours(-4));
        var audience = new NotificationAudience
        {
            Level = NotificationAudienceLevel.Selected,
            Roles = ["admin"],
            Filters = [new SubscriptionFilter { Context = "orders" }],
        };

        var result = await ResolveAsync(Sample.Item(Org), Sample.Config(audience));

        Assert.Equal(["subscribed"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Console_NamedRootUserWithProjectAccess_Receives_OthersDropped()
    {
        await _mongo.AddProjectPeopleAsync("root-member", NotificationMongo.TenantId);
        await _mongo.AddProjectPeopleAsync("root-elsewhere", "OTHER-PROJECT");
        await _mongo.AddUserAsync("project-user", [Org]);

        var result = await ResolveAsync(Sample.Item(Org, "root-member", "root-elsewhere", "project-user"),
            Sample.Config(scope: NotificationScope.Both));

        Assert.Equal(["root-member"], result.ConsoleUserIds);
        Assert.Equal(["project-user"], result.ProjectUserIds);
    }

    [Fact]
    public async Task Scope_App_SkipsConsoleUsers_Console_SkipsProjectUsers()
    {
        await _mongo.AddProjectPeopleAsync("root-member", NotificationMongo.TenantId);
        await _mongo.AddUserAsync("project-user", [Org]);
        var item = Sample.Item(Org, "root-member", "project-user");

        var app = await ResolveAsync(item, Sample.Config(scope: NotificationScope.App));
        var console = await ResolveAsync(item, Sample.Config(scope: NotificationScope.Console));

        Assert.Equal(["project-user"], app.ProjectUserIds);
        Assert.Empty(app.ConsoleUserIds);
        Assert.Empty(console.ProjectUserIds);
        Assert.Equal(["root-member"], console.ConsoleUserIds);
    }
}
