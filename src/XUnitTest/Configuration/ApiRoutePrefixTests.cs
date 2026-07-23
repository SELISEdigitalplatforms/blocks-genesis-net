using Blocks.Genesis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using System.Reflection;

namespace XUnitTest.Configuration;

/// <summary>
/// Tests for API route-prefix handling: the public <see cref="ApplicationConfigurations.NormalizeApiRoutePrefixValue"/>
/// normaliser and the internal <c>ApiRoutePrefixConvention</c> that rewrites controller route templates.
/// </summary>
public class ApiRoutePrefixTests
{
    [Theory]
    [InlineData(null, "api")]
    [InlineData("   ", "api")]
    [InlineData("///", "api")]
    [InlineData("off", "")]
    [InlineData("None", "")]
    [InlineData("FALSE", "")]
    [InlineData("/custom/", "custom")]
    [InlineData("v2", "v2")]
    public void NormalizeApiRoutePrefixValue_ShouldNormaliseExpected(string? input, string expected)
    {
        Assert.Equal(expected, ApplicationConfigurations.NormalizeApiRoutePrefixValue(input));
    }

    [Theory]
    [InlineData("api", "")]
    [InlineData("api/orders", "orders")]
    [InlineData("/api/orders/", "orders")]
    [InlineData("orders", "orders")]
    public void RemoveLeadingApiPrefix_ShouldStripApiSegment(string template, string expected)
    {
        var method = ConventionType.GetMethod("RemoveLeadingApiPrefix", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (string)method.Invoke(null, [template])!);
    }

    [Theory]
    [InlineData("api", "orders", "api/orders")]
    [InlineData("", "orders", "orders")]
    [InlineData("api", "", "api")]
    [InlineData("", "", "")]
    public void BuildFinalTemplate_ShouldJoinNonEmptySegments(string prefix, string route, string expected)
    {
        var method = ConventionType.GetMethod("BuildFinalTemplate", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (string)method.Invoke(null, [prefix, route])!);
    }

    [Fact]
    public void Apply_ShouldPrefixRoutedSelectors_AndSkipUnroutedOnes()
    {
        var convention = (IApplicationModelConvention)Activator.CreateInstance(ConventionType, "api")!;

        var controller = new ControllerModel(typeof(SampleController).GetTypeInfo(), new List<object>());
        var routed = new SelectorModel { AttributeRouteModel = new AttributeRouteModel(new RouteAttribute("api/orders")) };
        var unrouted = new SelectorModel();
        controller.Selectors.Add(routed);
        controller.Selectors.Add(unrouted);

        var application = new ApplicationModel();
        application.Controllers.Add(controller);

        convention.Apply(application);

        Assert.Equal("api/orders", routed.AttributeRouteModel!.Template);
        Assert.Null(unrouted.AttributeRouteModel);
    }

    private static Type ConventionType =>
        typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.ApiRoutePrefixConvention")!;

    private sealed class SampleController { }
}
