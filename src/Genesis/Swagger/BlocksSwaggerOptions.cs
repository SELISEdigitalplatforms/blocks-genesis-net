namespace Blocks.Genesis
{
    public class BlocksSwaggerOptions
    {
        public string Version { get; set; } = "v1";
        public string Title { get; set; } = "Blocks API";
        public string Description { get; set; } = "Detailed description of the API";
        public string TermsOfServiceUrl { get; set; }
        public ContactInfo Contact { get; set; }
        public LicenseInfo License { get; set; }
        public string XmlCommentsFilePath { get; set; }
        public string EndpointUrl { get; set; } = "/swagger/v1/swagger.json";
        public bool EnableBearerAuth { get; set; } = true;
    }

    public class ContactInfo
    {
        public string Name { get; set; }
        public string Email { get; set; }
        public string Url { get; set; }
    }

    public class LicenseInfo
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }
}