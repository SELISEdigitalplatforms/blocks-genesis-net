namespace Blocks.Genesis
{
    public class BlocksSwaggerOptions
    {
        public string Version { get; set; } = "v1";
        public string Title { get; set; } = "Blocks API";
        public string Description { get; set; } = "Detailed description of the API";
        public string XmlCommentsFilePath { get; set; } = string.Empty;
        public string EndpointUrl { get; set; } = "/swagger/v1/swagger.json";
        public string PathBase { get; set; } = string.Empty;
        public bool EnableBearerAuth { get; set; } = true;
        public string ServiceName { get; set; } = string.Empty;
    }

    public class ContactInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class LicenseInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }
}