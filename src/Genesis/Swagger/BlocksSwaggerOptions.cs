namespace Blocks.Genesis
{
    /// <summary>
    /// Configuration options for Blocks Swagger/OpenAPI documentation generation.
    /// </summary>
    public class BlocksSwaggerOptions
    {
        /// <summary>Gets or sets the API version string. Defaults to "v1".</summary>
        public string Version { get; set; } = "v1";

        /// <summary>Gets or sets the API title shown in the Swagger UI.</summary>
        public string Title { get; set; } = "Blocks API";

        /// <summary>Gets or sets the API description shown in the Swagger UI.</summary>
        public string Description { get; set; } = "Detailed description of the API";

        /// <summary>
        /// Gets or sets the path to the XML comments file for documentation.
        /// When null or empty, defaults to the executing assembly name with .xml extension.
        /// </summary>
        public string? XmlCommentsFilePath { get; set; }

        /// <summary>Gets or sets the Swagger JSON endpoint URL. Defaults to "/swagger/v1/swagger.json".</summary>
        public string EndpointUrl { get; set; } = "/swagger/v1/swagger.json";

        /// <summary>Gets or sets a value indicating whether JWT Bearer auth is shown in Swagger UI. Defaults to true.</summary>
        public bool EnableBearerAuth { get; set; } = true;

        /// <summary>
        /// Gets or sets the service name used to prefix all API paths.
        /// When set, all paths will be prefixed with /{ServiceName}/{Version}.
        /// </summary>
        public string? ServiceName { get; set; }
    }

    /// <summary>
    /// Contact information for the API, included in the OpenAPI info block.
    /// </summary>
    public class ContactInfo
    {
        /// <summary>Gets or sets the contact person or organization name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the contact email address.</summary>
        public string? Email { get; set; }

        /// <summary>Gets or sets the contact URL.</summary>
        public string? Url { get; set; }
    }

    /// <summary>
    /// License information for the API, included in the OpenAPI info block.
    /// </summary>
    public class LicenseInfo
    {
        /// <summary>Gets or sets the license name (e.g. "MIT", "Apache 2.0").</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the URL to the license text.</summary>
        public string? Url { get; set; }
    }
}