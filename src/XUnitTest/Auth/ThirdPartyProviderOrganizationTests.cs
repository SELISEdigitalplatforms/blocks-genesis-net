using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Xunit;

namespace XUnitTest.Auth;

/// <summary>
/// <see cref="ThirdPartyJwtProvider.DefaultOrganizationId"/> can never come out blank, whichever
/// way a value arrives.
/// </summary>
/// <remarks>
/// A blank organization is not a neutral value: Genesis's endpoint authorization handler collapses
/// one to <c>"default"</c>, which is the tenant-wide scope, while the organization endpoints deny
/// it outright. So a blank would leave the scope a caller receives depending on which layer read
/// it first. Normalising on the property means the writers -- the OS save path, and BSON -- do not
/// each have to remember.
/// </remarks>
public class ThirdPartyProviderOrganizationTests
{
    [Fact]
    public void UnsetOnANewProvider_IsTheDefaultOrganization()
    {
        Assert.Equal(
            ThirdPartyJwtProvider.DefaultOrganization,
            new ThirdPartyJwtProvider().DefaultOrganizationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankAssignment_FallsBackToTheDefaultOrganization(string? assigned)
    {
        // The case a form produces: an input left empty posts "", not nothing at all.
        var provider = new ThirdPartyJwtProvider { DefaultOrganizationId = "org-7" };

        provider.DefaultOrganizationId = assigned!;

        Assert.Equal(ThirdPartyJwtProvider.DefaultOrganization, provider.DefaultOrganizationId);
    }

    [Fact]
    public void ARealValueIsKept_AndTrimmed()
    {
        var provider = new ThirdPartyJwtProvider { DefaultOrganizationId = "  org-7 " };

        // Trimmed because the comparison downstream is ordinal and exact: " org-7" would match no
        // organization at all, and would do it silently.
        Assert.Equal("org-7", provider.DefaultOrganizationId);
    }

    [Fact]
    public void ADocumentWrittenBeforeTheFieldExisted_ReadsAsTheDefaultOrganization()
    {
        // No element at all, so the property keeps the value it was initialised with. This is every
        // provider row stored to date, and is why no migration is needed.
        var document = new BsonDocument { { "_id", "p1" }, { "Key", "auth0-web" } };

        var provider = BsonSerializer.Deserialize<ThirdPartyJwtProvider>(document);

        Assert.Equal(ThirdPartyJwtProvider.DefaultOrganization, provider.DefaultOrganizationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankElementIsNormalisedOnDeserialisation(string stored)
    {
        // The element is present, so it overwrites the initialiser -- the initialiser alone would
        // leave this blank. Passing proves the driver assigns through the property setter rather
        // than the backing field, which is what makes the normalisation reachable at all.
        var document = new BsonDocument { { "_id", "p1" }, { "DefaultOrganizationId", stored } };

        var provider = BsonSerializer.Deserialize<ThirdPartyJwtProvider>(document);

        Assert.Equal(ThirdPartyJwtProvider.DefaultOrganization, provider.DefaultOrganizationId);
    }

    [Fact]
    public void ANullElementIsNormalisedOnDeserialisation()
    {
        var document = new BsonDocument { { "_id", "p1" }, { "DefaultOrganizationId", BsonNull.Value } };

        var provider = BsonSerializer.Deserialize<ThirdPartyJwtProvider>(document);

        Assert.Equal(ThirdPartyJwtProvider.DefaultOrganization, provider.DefaultOrganizationId);
    }

    [Fact]
    public void AConfiguredValueSurvivesARoundTrip()
    {
        // What the OS save path does: read the row, change one field, replace the document. The
        // organization has to come back out of that unchanged.
        var stored = new ThirdPartyJwtProvider { Key = "auth0-web", DefaultOrganizationId = "org-7" }
            .ToBsonDocument();

        var provider = BsonSerializer.Deserialize<ThirdPartyJwtProvider>(stored);

        Assert.Equal("org-7", provider.DefaultOrganizationId);
    }
}
