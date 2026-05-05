using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Blocks.Genesis;

internal sealed class ApiRoutePrefixConvention(string routePrefix) : IApplicationModelConvention
{
    private readonly string _routePrefix = routePrefix;

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var selector in controller.Selectors)
            {
                if (selector.AttributeRouteModel == null)
                {
                    continue;
                }

                var currentTemplate = selector.AttributeRouteModel.Template ?? string.Empty;
                var routeWithoutApiPrefix = RemoveLeadingApiPrefix(currentTemplate);
                var finalTemplate = BuildFinalTemplate(_routePrefix, routeWithoutApiPrefix);

                selector.AttributeRouteModel = new AttributeRouteModel(new RouteAttribute(finalTemplate));
            }
        }
    }

    private static string BuildFinalTemplate(string routePrefix, string routeWithoutApiPrefix)
    {
        var segments = new List<string>();

        if (!string.IsNullOrWhiteSpace(routePrefix))
        {
            segments.Add(routePrefix);
        }

        if (!string.IsNullOrWhiteSpace(routeWithoutApiPrefix))
        {
            segments.Add(routeWithoutApiPrefix);
        }

        return string.Join('/', segments);
    }

    private static string RemoveLeadingApiPrefix(string template)
    {
        var trimmed = (template ?? string.Empty).Trim('/');

        if (trimmed.Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[4..];
        }

        return trimmed;
    }

}
