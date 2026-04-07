using Blocks.Genesis;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Blocks.Genesis
{
    /// <summary>
    /// Swagger document filter that prefixes all API paths with the service name and version.
    /// Enables service-based path routing in the generated OpenAPI document.
    /// </summary>
    public class AddServiceVersionToPathsFilter : IDocumentFilter
    {
        private readonly BlocksSwaggerOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AddServiceVersionToPathsFilter"/> class.
        /// </summary>
        /// <param name="options">Swagger options containing the service name and version.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
        public AddServiceVersionToPathsFilter(BlocksSwaggerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Applies the path prefix transformation to all paths in the Swagger document.
        /// </summary>
        /// <param name="swaggerDoc">The OpenAPI document to transform.</param>
        /// <param name="context">The document filter context.</param>
        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            if (swaggerDoc is null)
            {
                return;
            }

            var updatedPaths = new OpenApiPaths();

            foreach (var path in swaggerDoc.Paths)
            {
                var newKey = $"/{_options.ServiceName}/{_options.Version}{path.Key}";
                updatedPaths.Add(newKey, path.Value);
            }

            swaggerDoc.Paths = updatedPaths;
        }
    }
}
