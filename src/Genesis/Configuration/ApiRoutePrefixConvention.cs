using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Blocks.Genesis;

internal sealed class ApiRoutePrefixConvention(string routePrefix, string serviceSegment) : IApplicationModelConvention
{
    private readonly string _routePrefix = routePrefix;
    private readonly string _serviceSegment = serviceSegment;

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
                var routeWithoutServicePrefix = RemoveLeadingServiceSegment(routeWithoutApiPrefix, _serviceSegment);
                var finalTemplate = BuildFinalTemplate(_routePrefix, _serviceSegment, routeWithoutServicePrefix);

                selector.AttributeRouteModel = new AttributeRouteModel(new RouteAttribute(finalTemplate));
            }
        }
    }

    private static string BuildFinalTemplate(string routePrefix, string serviceSegment, string routeWithoutApiPrefix)
    {
        var segments = new List<string>();

        if (!string.IsNullOrWhiteSpace(routePrefix))
        {
            segments.Add(routePrefix);
        }

        if (!string.IsNullOrWhiteSpace(serviceSegment))
        {
            segments.Add(serviceSegment);
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

    private static string RemoveLeadingServiceSegment(string template, string serviceSegment)
    {
        var trimmed = (template ?? string.Empty).Trim('/');

        if (string.IsNullOrWhiteSpace(serviceSegment))
        {
            return trimmed;
        }

        if (trimmed.Equals(serviceSegment, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith($"{serviceSegment}/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[(serviceSegment.Length + 1)..];
        }

        return trimmed;
    }
}
