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

        foreach (var field in new[] { "IsBuiltIn", "IsDisabled", "Description", "Importance", "Kind", "SourceService", "ConsumerAppRecommendation", "ConsumerAppNote", "Scope", "Audience", "DefaultAudience", "AudienceLocked", "RateLimitPerMinute", "LiveEnabled", "PushEnabled", "PushTemplate" })
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
        Assert.Null(read.DefaultAudience);
        Assert.False(read.AudienceLocked);
        Assert.Equal(0, read.RateLimitPerMinute);
        Assert.Equal(NotificationAudienceLevel.AffectedOnly, read.EffectiveAudience().Level);
    }

    [Fact]
    public void Receivers_RoundTrip()
    {
        var c = Custom();
        c.Scope = NotificationScope.Both;
        c.AudienceLocked = true;
        c.RateLimitPerMinute = 60;
        c.DefaultAudience = new NotificationAudience { Level = NotificationAudienceLevel.AllUsers };
        c.Audience = new NotificationAudience
        {
            Level = NotificationAudienceLevel.Selected,
            Match = NotificationAudienceMatch.All,
            UserIds = ["u1"],
            Roles = ["admin"],
            Permissions = ["blocks-iam::iam::users"],
            Filters = [new Blocks.Genesis.Entities.Logic.SubscriptionFilter { Context = "orders", ActionName = "paid", Value = "42" }],
            AlsoNamedUsers = true,
        };

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(c.ToBsonDocument());

        Assert.Equal(NotificationScope.Both, read.Scope);
        Assert.True(read.AudienceLocked);
        Assert.Equal(60, read.RateLimitPerMinute);
        Assert.Equal(NotificationAudienceLevel.AllUsers, read.DefaultAudience!.Level);
        var a = read.Audience!;
        Assert.Equal(NotificationAudienceLevel.Selected, a.Level);
        Assert.Equal(NotificationAudienceMatch.All, a.Match);
        Assert.Equal(["u1"], a.UserIds);
        Assert.Equal(["admin"], a.Roles);
        Assert.Equal(["blocks-iam::iam::users"], a.Permissions);
        Assert.Equal("paid", Assert.Single(a.Filters).ActionName);
        Assert.True(a.AlsoNamedUsers);
    }

    [Fact]
    public void EffectiveAudience_TenantWins_UnlessLocked()
    {
        var c = Custom();
        c.DefaultAudience = new NotificationAudience { Level = NotificationAudienceLevel.AllUsers };
        c.Audience = new NotificationAudience { Level = NotificationAudienceLevel.Selected, Roles = ["admin"] };

        Assert.Equal(NotificationAudienceLevel.Selected, c.EffectiveAudience().Level);

        c.AudienceLocked = true;
        Assert.Equal(NotificationAudienceLevel.AllUsers, c.EffectiveAudience().Level);
    }

    [Fact]
    public void EffectiveAudience_NothingSet_IsAffectedOnly()
    {
        Assert.Equal(NotificationAudienceLevel.AffectedOnly, Custom().EffectiveAudience().Level);
    }

    [Fact]
    public void HasRules_FiltersAlone_DoNotCount()
    {
        Assert.True(new NotificationAudience { Roles = ["admin"] }.HasRules());
        Assert.False(new NotificationAudience { Filters = [new Blocks.Genesis.Entities.Logic.SubscriptionFilter { Context = "c" }] }.HasRules());
    }

    [Fact]
    public void Scope_Both_StoredAsTwo()
    {
        var c = Custom();
        c.Scope = NotificationScope.Both;

        Assert.Equal(2, c.ToBsonDocument()["Scope"].AsInt32);
    }

    [Fact]
    public void OldDocument_WithoutChannelFields_ReadsLiveOn_PushOff()
    {
        var old = new BsonDocument { { "_id", "c1" }, { "Name", "orders" }, { "ChannelToNotify", 0 }, { "NotificationType", 2 }, { "NotifyMethod", "orders" }, { "EnablePersistence", true } };

        var read = BsonSerializer.Deserialize<NotificationConfiguration>(old);

        Assert.True(read.LiveEnabled);
        Assert.False(read.PushEnabled);
        Assert.Null(read.PushTemplate);
        Assert.Equal(new NotificationChannels(Live: true, Push: false, Inbox: true), read.EffectiveChannels());
    }

    [Fact]
    public void LiveOff_IsStored_AndReadBackOff()
    {
        var c = Custom();
        c.LiveEnabled = false;

        var doc = c.ToBsonDocument();

        Assert.False(doc["LiveEnabled"].AsBoolean);
        Assert.False(BsonSerializer.Deserialize<NotificationConfiguration>(doc).LiveEnabled);
    }

    [Fact]
    public void OldFirebaseConfig_MapsToPushOnly()
    {
        var c = Custom();
        c.ChannelToNotify = NotifierTypes.Firebase;

        Assert.Equal(new NotificationChannels(Live: false, Push: true, Inbox: true), c.EffectiveChannels());
    }

    [Fact]
    public void NewChannelFields_WinOverOldFirebaseValue()
    {
        var c = Custom();
        c.ChannelToNotify = NotifierTypes.Firebase;
        c.PushEnabled = true;
        c.EnablePersistence = false;

        Assert.Equal(new NotificationChannels(Live: true, Push: true, Inbox: false), c.EffectiveChannels());
    }

    [Fact]
    public void PushTemplate_RoundTrip_LinkLeftOutWhenNull()
    {
        var c = Custom();
        c.PushEnabled = true;
        c.PushTemplate = new NotificationPushTemplate { TitleKey = "orders.paid.title", BodyKey = "orders.paid.body" };

        var doc = c.ToBsonDocument();
        var read = BsonSerializer.Deserialize<NotificationConfiguration>(doc);

        Assert.False(doc["PushTemplate"].AsBsonDocument.Contains("Link"));
        Assert.True(read.PushEnabled);
        Assert.Equal("orders.paid.title", read.PushTemplate!.TitleKey);
        Assert.Equal("orders.paid.body", read.PushTemplate.BodyKey);
    }
}
