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

        foreach (var field in new[] { "IsBuiltIn", "IsDisabled", "Description", "Importance", "Kind", "SourceService", "ConsumerAppRecommendation", "ConsumerAppNote" })
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
}
