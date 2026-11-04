using Blocks.Genesis;
using Blocks.Genesis.Entities.Os;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace XUnitTest.Entities.Os;

#pragma warning disable CA1707 // same naming as the entity tests around them

/// <summary>
/// Organization-wise email templates (blocks-genesis-net#194): EmailTemplate.CreatedFromDefault and
/// MailServerConfiguration.IsOrganizationDefault are stored booleans that old documents do not have.
/// </summary>
public class OrganizationEmailFieldsTests
{
    private static T Read<T>(BsonDocument doc) => BsonSerializer.Deserialize<T>(doc);

    private static BsonDocument Write<T>(T value) => value.ToBsonDocument();

    // H1
    [Fact]
    public void EmailTemplate_New_CreatedFromDefaultIsFalse() => Assert.False(new EmailTemplate().CreatedFromDefault);

    // H2, example 2
    [Fact]
    public void EmailTemplate_CreatedFromDefaultTrue_WritesElement()
    {
        var doc = Write(new EmailTemplate { ItemId = "t2", CreatedFromDefault = true });

        Assert.Equal(BsonBoolean.True, doc["CreatedFromDefault"]);
    }

    // H3
    [Fact]
    public void EmailTemplate_StoredTrue_ReadsTrue()
    {
        var doc = new BsonDocument { { "_id", "t1" }, { "Name", "OrderConfirmation" }, { "CreatedFromDefault", true } };

        Assert.True(Read<EmailTemplate>(doc).CreatedFromDefault);
    }

    // H4
    [Fact]
    public void MailServerConfiguration_New_IsOrganizationDefaultIsFalse() => Assert.False(new MailServerConfiguration().IsOrganizationDefault);

    // H5
    [Fact]
    public void MailServerConfiguration_IsOrganizationDefaultTrue_WritesElement()
    {
        var doc = Write(new MailServerConfiguration { ItemId = "c1", IsOrganizationDefault = true });

        Assert.Equal(BsonBoolean.True, doc["IsOrganizationDefault"]);
    }

    // H6, example 3
    [Fact]
    public void MailServerConfiguration_StoredTrue_ReadsTrue()
    {
        var doc = new BsonDocument { { "_id", "c1" }, { "Name", "Acme SMTP" }, { "IsOrganizationDefault", true } };

        Assert.True(Read<MailServerConfiguration>(doc).IsOrganizationDefault);
    }

    // H7: every existing value survives a round-trip, with either flag value.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmailTemplate_RoundTrip_KeepsExistingValues(bool createdFromDefault)
    {
        var original = new EmailTemplate
        {
            ItemId = "t1",
            Name = "OrderConfirmation",
            Language = "en-US",
            TemplateSubject = "Your order",
            TemplateBody = "<p>Thanks</p>",
            JsonContent = "{\"blocks\":[]}",
            MailConfigurationId = "c1",
            ImageId = "img1",
            ImageUrl = "https://example.test/img1.png",
            GeneratedBy = "ai",
            OrganizationId = "acme",
            CreatedFromDefault = createdFromDefault,
        };

        var read = Read<EmailTemplate>(Write(original));

        Assert.Equal(original.Name, read.Name);
        Assert.Equal(original.Language, read.Language);
        Assert.Equal(original.TemplateSubject, read.TemplateSubject);
        Assert.Equal(original.TemplateBody, read.TemplateBody);
        Assert.Equal(original.JsonContent, read.JsonContent);
        Assert.Equal(original.MailConfigurationId, read.MailConfigurationId);
        Assert.Equal(original.ImageId, read.ImageId);
        Assert.Equal(original.ImageUrl, read.ImageUrl);
        Assert.Equal(original.GeneratedBy, read.GeneratedBy);
        Assert.Equal(original.OrganizationId, read.OrganizationId);
        Assert.Equal(createdFromDefault, read.CreatedFromDefault);
    }

    // H8: every existing value survives a round-trip, with either flag value.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MailServerConfiguration_RoundTrip_KeepsExistingValues(bool isOrganizationDefault)
    {
        var original = new MailServerConfiguration
        {
            ItemId = "c1",
            Name = "Acme SMTP",
            IsDefault = true,
            IsInbound = true,
            Provider = MailServiceProvider.Office365Smtp,
            Host = "smtp.example.test",
            Port = 587,
            SenderAddress = "noreply@example.test",
            AccountPasswordReference = "secret-1",
            OrganizationId = "acme",
            IsOrganizationDefault = isOrganizationDefault,
        };

        var read = Read<MailServerConfiguration>(Write(original));

        Assert.Equal(original.IsDefault, read.IsDefault);
        Assert.Equal(original.IsInbound, read.IsInbound);
        Assert.Equal(original.Provider, read.Provider);
        Assert.Equal(original.Host, read.Host);
        Assert.Equal(original.Port, read.Port);
        Assert.Equal(original.SenderAddress, read.SenderAddress);
        Assert.Equal(original.AccountPasswordReference, read.AccountPasswordReference);
        Assert.Equal(original.OrganizationId, read.OrganizationId);
        Assert.Equal(isOrganizationDefault, read.IsOrganizationDefault);
    }

    // C1, C3, example 1: an old template document has neither the flag nor an organization.
    [Fact]
    public void EmailTemplate_OldDocument_ReadsFalseAndDefaultOrganization()
    {
        var doc = new BsonDocument { { "_id", "t1" }, { "Name", "OrderConfirmation" }, { "Language", "en-US" } };

        var read = Read<EmailTemplate>(doc);

        Assert.False(read.CreatedFromDefault);
        Assert.Equal("default", read.OrganizationId);
    }

    // C2, example 4
    [Fact]
    public void MailServerConfiguration_OldDocument_ReadsFalse()
    {
        var doc = new BsonDocument { { "_id", "c2" }, { "Name", "Default" }, { "IsDefault", true } };

        var read = Read<MailServerConfiguration>(doc);

        Assert.False(read.IsOrganizationDefault);
        Assert.True(read.IsDefault);
    }

    // C4, example 5
    [Fact]
    public void EmailTemplate_UnknownElement_Ignored()
    {
        var doc = new BsonDocument { { "_id", "t3" }, { "OrganizationIds", new BsonArray { "x" } } };

        Assert.False(Read<EmailTemplate>(doc).CreatedFromDefault);
    }

    // C4
    [Fact]
    public void MailServerConfiguration_UnknownElement_Ignored()
    {
        var doc = new BsonDocument { { "_id", "c3" }, { "OrganizationIds", new BsonArray { "x" } } };

        Assert.False(Read<MailServerConfiguration>(doc).IsOrganizationDefault);
    }

    private static readonly Dictionary<string, BsonValue> Mistyped = new(StringComparer.Ordinal)
    {
        ["string yes"] = new BsonString("yes"),
        ["document"] = new BsonDocument("a", 1),
        ["array"] = new BsonArray { 1 },
        ["date"] = new BsonDateTime(0),
        ["int 1"] = new BsonInt32(1),
        ["int 0"] = new BsonInt32(0),
        ["double"] = new BsonDouble(2.5),
        ["string true"] = new BsonString("true"),
        ["null"] = BsonNull.Value,
    };

    public static TheoryData<string> MistypedBooleans()
    {
        var data = new TheoryData<string>();
        foreach (var key in Mistyped.Keys)
            data.Add(key);
        return data;
    }

    // C5: no special handling, so a mistyped value fails (or converts) exactly as it does for the existing IsDefault.
    [Theory]
    [MemberData(nameof(MistypedBooleans))]
    public void EmailTemplate_MistypedFlag_BehavesLikeAnExistingBoolean(string value) =>
        AssertSameAsExistingBoolean<EmailTemplate>(Mistyped[value], "CreatedFromDefault", t => t.CreatedFromDefault);

    // C5
    [Theory]
    [MemberData(nameof(MistypedBooleans))]
    public void MailServerConfiguration_MistypedFlag_BehavesLikeAnExistingBoolean(string value) =>
        AssertSameAsExistingBoolean<MailServerConfiguration>(Mistyped[value], "IsOrganizationDefault", c => c.IsOrganizationDefault);

    private static void AssertSameAsExistingBoolean<T>(BsonValue value, string element, Func<T, bool> flag)
    {
        var expected = Outcome<MailServerConfiguration>(new BsonDocument { { "_id", "x" }, { "IsDefault", value } }, c => c.IsDefault);
        var actual = Outcome(new BsonDocument { { "_id", "x" }, { element, value } }, flag);

        Assert.Equal(expected, actual);
    }

    private static string Outcome<T>(BsonDocument doc, Func<T, bool> flag)
    {
        try
        {
            return flag(Read<T>(doc)).ToString();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or BsonSerializationException)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    // C6: a consumer still on the previous package (4.6.0 shape, no new property) reads documents with the new fields.
    [BsonIgnoreExtraElements]
    private sealed class PreviousEmailTemplate : BaseEntity
    {
        public string? Name { get; set; }
        public string? TemplateSubject { get; set; }
    }

    [BsonIgnoreExtraElements]
    private sealed class PreviousMailServerConfiguration : BaseEntity
    {
        public string? Name { get; set; }
        public bool IsDefault { get; set; }
    }

    // C6
    [Fact]
    public void PreviousPackageShape_ReadsNewTemplateDocument()
    {
        var doc = Write(new EmailTemplate { ItemId = "t1", Name = "Welcome", TemplateSubject = "Hi", OrganizationId = "acme", CreatedFromDefault = true });

        var read = Read<PreviousEmailTemplate>(doc);

        Assert.Equal("Welcome", read.Name);
        Assert.Equal("Hi", read.TemplateSubject);
        Assert.Equal("acme", read.OrganizationId);
    }

    // C6
    [Fact]
    public void PreviousPackageShape_ReadsNewConfigurationDocument()
    {
        var doc = Write(new MailServerConfiguration { ItemId = "c1", Name = "Acme SMTP", IsDefault = true, IsOrganizationDefault = true });

        var read = Read<PreviousMailServerConfiguration>(doc);

        Assert.Equal("Acme SMTP", read.Name);
        Assert.True(read.IsDefault);
    }
}
