using Blocks.Genesis.Entities.Os;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace XUnitTest.Entities.Os;

public class NotificationConfigurationBuiltInTests
{
    private static NotificationConfiguration Custom() => new()
    {
        ItemId = "c1",
        Name = "orders",
        ChannelToNotify = NotifierTypes.SignalR,
        NotificationType = NotificationReceiverTypes.UserSpecificReceiverType,
        NotifyMethod = "orders",
        EnablePersistence = true,
    };

    [Fact]
    public void CustomConfig_WritesNoneOfTheBuiltInFields()
    {
        var doc = Custom().ToBsonDocument();

        foreach (var field in new[] { "IsBuiltIn", "IsDisabled", "Description", "Importance", "Kind", "SourceService", "ConsumerAppRecommendation", "ConsumerAppNote", "Scope", "Audience" })
        {
            Assert.False(doc.Contains(field), field);
        }
    }

    [Fact]
    public void OldDocument_WithoutTheFields_ReadsAsEnabledCustomConfig()
    {
        var old = new BsonDocument { { "_id", "c1" }, { "Name", "orders" }, { "ChannelToNotify", 0 }, { "NotificationType", 2 }, { "NotifyMethod", "orders" }, { "EnablePersistence", true } };

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(old);

        Assert.False(read.IsBuiltIn);
        Assert.False(read.IsDisabled);
        Assert.Equal(NotificationKind.None, read.Kind);
        Assert.Equal(NotificationImportance.None, read.Importance);
        Assert.Null(read.Description);
        Assert.True(read.CanBeDisabled());
    }

    [Fact]
    public void BuiltInFields_RoundTrip()
    {
        var c = Custom();
        c.IsBuiltIn = true;
        c.IsDisabled = true;
        c.Description = "d";
        c.Importance = NotificationImportance.High;
        c.Kind = NotificationKind.Security;
        c.SourceService = "blocks-iam";
        c.ConsumerAppRecommendation = "r";
        c.ConsumerAppNote = "n";

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(c.ToBsonDocument());

        Assert.True(read.IsBuiltIn);
        Assert.True(read.IsDisabled);
        Assert.Equal("d", read.Description);
        Assert.Equal(NotificationImportance.High, read.Importance);
        Assert.Equal(NotificationKind.Security, read.Kind);
        Assert.Equal("blocks-iam", read.SourceService);
        Assert.Equal("r", read.ConsumerAppRecommendation);
        Assert.Equal("n", read.ConsumerAppNote);
    }

    [Theory]
    [InlineData(NotificationKind.Job, false)]
    [InlineData(NotificationKind.Progress, false)]
    [InlineData(NotificationKind.Alert, true)]
    [InlineData(NotificationKind.Security, true)]
    [InlineData(NotificationKind.Access, true)]
    [InlineData(NotificationKind.None, true)]
    public void CanBeDisabled_BuiltIn_DependsOnKind(NotificationKind kind, bool expected)
    {
        var c = Custom();
        c.IsBuiltIn = true;
        c.Kind = kind;

        Assert.Equal(expected, c.CanBeDisabled());
    }

    [Theory]
    [InlineData(NotificationKind.Job)]
    [InlineData(NotificationKind.Progress)]
    public void CanBeDisabled_CustomConfig_AlwaysTrue(NotificationKind kind)
    {
        var c = Custom();
        c.Kind = kind;

        Assert.True(c.CanBeDisabled());
    }

    [Fact]
    public void OldDocument_ReadsAsAppScopeWithNoAudience()
    {
        var old = new BsonDocument { { "_id", "c1" }, { "Name", "orders" }, { "ChannelToNotify", 0 }, { "NotificationType", 2 }, { "NotifyMethod", "orders" }, { "EnablePersistence", true } };

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(old);

        Assert.Equal(NotificationScope.App, read.Scope);
        Assert.Null(read.Audience);
    }

    [Fact]
    public void ScopeAndAudience_RoundTrip()
    {
        var c = Custom();
        c.Scope = NotificationScope.Console;
        c.Audience = new NotificationAudience
        {
            Match = NotificationAudienceMatch.All,
            UserIds = ["u1"],
            OrganizationIds = ["o1"],
            Roles = ["admin"],
            Permissions = ["blocks-iam::iam::users"],
            Filters = [new Blocks.Genesis.Entities.Logic.SubscriptionFilter { Context = "orders", ActionName = "paid", Value = "42" }],
        };

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(c.ToBsonDocument());

        Assert.Equal(NotificationScope.Console, read.Scope);
        Assert.NotNull(read.Audience);
        Assert.Equal(NotificationAudienceMatch.All, read.Audience!.Match);
        Assert.Equal(["u1"], read.Audience.UserIds);
        Assert.Equal(["o1"], read.Audience.OrganizationIds);
        Assert.Equal(["admin"], read.Audience.Roles);
        Assert.Equal(["blocks-iam::iam::users"], read.Audience.Permissions);
        Assert.Equal("paid", Assert.Single(read.Audience.Filters).ActionName);
    }

    [Fact]
    public void Audience_EveryoneInTenant_IsSetWithoutRules()
    {
        var a = new NotificationAudience { EveryoneInTenant = true };

        Assert.True(a.IsSet());
        Assert.False(a.HasRules());
        Assert.False(new NotificationAudience().IsSet());
    }

    [Fact]
    public void Audience_AnyList_CountsAsRule()
    {
        Assert.True(new NotificationAudience { Roles = ["admin"] }.HasRules());
        Assert.True(new NotificationAudience { Filters = [new Blocks.Genesis.Entities.Logic.SubscriptionFilter { Context = "c" }] }.HasRules());
    }

    [Fact]
    public void Scope_Both_RoundTrips()
    {
        var c = Custom();
        c.Scope = NotificationScope.Both;

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(c.ToBsonDocument());

        Assert.Equal(NotificationScope.Both, read.Scope);
        Assert.Equal(2, c.ToBsonDocument()["Scope"].AsInt32);
    }
}
