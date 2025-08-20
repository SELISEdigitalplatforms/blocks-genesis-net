using Blocks.Genesis;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public class AddServiceVersionToPathsFilter : IDocumentFilter
{
    private readonly BlocksSwaggerOptions _options;

    public AddServiceVersionToPathsFilter(BlocksSwaggerOptions options)
    {
        _options = options;
    }

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var updatedPaths = new OpenApiPaths();

        foreach (var path in swaggerDoc.Paths)
        {
            // Prefix each endpoint with /{service}/{version}
            var newKey = $"/{_options.ServiceName}/{_options.Version}{path.Key}";
            updatedPaths.Add(newKey, path.Value);
        }

        swaggerDoc.Paths = updatedPaths;
    }
}
