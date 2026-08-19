using Blocks.Genesis;
using Moq;

namespace XUnitTest.Delegation;

/// <summary>
/// Shared delegation doubles. Most message-client and worker tests predate delegated access and
/// only need the dependency satisfied, so the defaults here behave as "no grant in play".
/// </summary>
internal static class DelegationTestDoubles
{
    /// <summary>A factory that never produces a grant — the shape of an unauthenticated send.</summary>
    public static IDelegationGrantFactory NoGrantFactory()
    {
        var factory = new Mock<IDelegationGrantFactory>();
        factory.Setup(f => f.CreateForSendAsync(It.IsAny<TimeSpan?>())).ReturnsAsync((string?)null);
        return factory.Object;
    }

    /// <summary>A factory that always produces <paramref name="grantId"/>.</summary>
    public static Mock<IDelegationGrantFactory> GrantFactory(string grantId)
    {
        var factory = new Mock<IDelegationGrantFactory>();
        factory.Setup(f => f.CreateForSendAsync(It.IsAny<TimeSpan?>())).ReturnsAsync(grantId);
        return factory;
    }

    public static IDelegationGrantStore NoOpStore() => new Mock<IDelegationGrantStore>().Object;

    public static IDelegatedTokenProvider NoOpProvider() => new Mock<IDelegatedTokenProvider>().Object;

    /// <summary>A well-formed grant id: <c>dg_</c> plus 64 lowercase hex chars.</summary>
    public static string SampleGrantId(char fill = 'a')
        => DelegationConstants.GrantIdPrefix + new string(fill, DelegationConstants.GrantIdRandomBytes * 2);
}
