using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Observability;

/// <summary>
/// Covers <see cref="BlocksRollbar"/> in the state every service starts in: unconfigured.
/// </summary>
/// <remarks>
/// Deliberately limited to the disabled paths. <c>Initialize</c> with a real token brings up a
/// process-wide singleton that cannot be torn down and would attempt network transmission, so
/// exercising the enabled path here would leak into every other test in the run. The enabled path
/// is verified against a live token in an app instead.
/// <para>
/// The behaviour these tests protect is the one that matters for a shared package: a service that
/// has not been seeded must be completely unaffected.
/// </para>
/// </remarks>
public class BlocksRollbarTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Initialize_ShouldLeaveReportingOff_WhenNoAccessTokenIsConfigured()
    {
        BlocksRollbar.Initialize(Configuration(), "blocks-os", "dev");

        Assert.False(BlocksRollbar.IsEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Initialize_ShouldLeaveReportingOff_WhenAccessTokenIsBlank(string token)
    {
        BlocksRollbar.Initialize(Configuration(("Rollbar:AccessToken", token)), "blocks-os", "dev");

        Assert.False(BlocksRollbar.IsEnabled);
    }

    [Fact]
    public void Initialize_ShouldRejectNullConfiguration()
    {
        Assert.Throws<ArgumentNullException>(
            () => BlocksRollbar.Initialize(null!, "blocks-os", "dev"));
    }

    [Fact]
    public void AttachDiagnostics_ShouldRejectNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => BlocksRollbar.AttachDiagnostics(null!));
    }

    [Fact]
    public void AttachDiagnostics_ShouldSayReportingIsOff_WhenUnconfigured()
    {
        var logger = new Mock<ILogger>();

        BlocksRollbar.AttachDiagnostics(logger.Object);

        // The point of the line: an operator can tell "no token seeded" apart from "seeded but
        // failing to deliver" without guessing.
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("OFF")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_ShouldDoNothing_WhenReportingIsOff()
    {
        // Would throw inside Rollbar's locator if the disabled guard were missing, which is exactly
        // the regression that would take down every unseeded service.
        BlocksRollbar.Report(
            new InvalidOperationException("boom"),
            new DefaultHttpContext(),
            StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public void Report_ShouldTolerateNoHttpContext()
    {
        BlocksRollbar.Report(new InvalidOperationException("boom"), null, 500);
    }

    [Fact]
    public void Report_ShouldTolerateNullException()
    {
        BlocksRollbar.Report(null!, null, 500);
    }
}
