using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace XUnitTest.Swagger;

public class BlocksSwaggerTests
{
    [Fact]
    public void BlocksSwaggerOptions_ShouldExposeDefaultValues_AndAllowSetters()
    {
        var options = new BlocksSwaggerOptions();

        Assert.Equal("v1", options.Version);
        Assert.Equal("Blocks API", options.Title);
        Assert.Equal("Detailed description of the API", options.Description);
        Assert.Equal("/swagger/v1/swagger.json", options.EndpointUrl);
        Assert.True(options.EnableBearerAuth);

        var contact = new ContactInfo
        {
            Name = "Jane",
            Email = "jane@example.com",
            Url = "https://example.com"
        };

        var license = new LicenseInfo
        {
            Name = "MIT",
            Url = "https://opensource.org/license/mit"
        };

        Assert.Equal("Jane", contact.Name);
        Assert.Equal("jane@example.com", contact.Email);
        Assert.Equal("https://example.com", contact.Url);
        Assert.Equal("MIT", license.Name);
        Assert.Equal("https://opensource.org/license/mit", license.Url);
    }

    [Fact]
    public void AddServiceVersionToPathsFilter_ShouldPrefixAllPaths()
    {
        var options = new BlocksSwaggerOptions
        {
            ServiceName = "catalog",
            Version = "v2"
        };

        var filter = new AddServiceVersionToPathsFilter(options);
        var document = new OpenApiDocument
        {
            Paths = new OpenApiPaths
            {
                ["/orders"] = new OpenApiPathItem(),
                ["/orders/{id}"] = new OpenApiPathItem()
            }
        };

        filter.Apply(document, context: null!);

        Assert.Equal(2, document.Paths.Count);
        Assert.Contains("/catalog/v2/orders", document.Paths.Keys);
        Assert.Contains("/catalog/v2/orders/{id}", document.Paths.Keys);
        Assert.DoesNotContain("/orders", document.Paths.Keys);
    }

    [Fact]
    public void AddBlocksSwagger_ShouldNotThrow_WhenOptionsIsNull()
    {
        var services = new ServiceCollection();

        var ex = Record.Exception(() => services.AddBlocksSwagger(null!));

        Assert.Null(ex);
    }

    [Fact]
    public void AddBlocksSwagger_ShouldRegisterDefaultHeader_AndSkipBearerAndDocumentFilter_WhenDisabledOrEmptyService()
    {
        EnsureXmlCommentsFile("swagger-disabled.xml");

        var services = new ServiceCollection();
        services.AddOptions();

        var options = new BlocksSwaggerOptions
        {
            Version = "v5",
            Title = "No Auth API",
            Description = "Auth disabled test",
            XmlCommentsFilePath = "swagger-disabled.xml",
            EnableBearerAuth = false,
            ServiceName = string.Empty
        };

        services.AddBlocksSwagger(options);

        using var provider = services.BuildServiceProvider();
        var swaggerOptions = provider.GetRequiredService<IOptions<SwaggerGenOptions>>().Value;

        Assert.Contains("v5", swaggerOptions.SwaggerGeneratorOptions.SwaggerDocs.Keys);
        Assert.Contains(BlocksConstants.BlocksKey, swaggerOptions.SwaggerGeneratorOptions.SecuritySchemes.Keys);
        Assert.DoesNotContain(JwtBearerDefaults.AuthenticationScheme, swaggerOptions.SwaggerGeneratorOptions.SecuritySchemes.Keys);
        Assert.DoesNotContain(
            swaggerOptions.DocumentFilterDescriptors,
            d => d.Type == typeof(AddServiceVersionToPathsFilter));
    }

    [Fact]
    public void AddBlocksSwagger_ShouldRegisterBearerAndDocumentFilter_WhenEnabledAndServiceProvided()
    {
        EnsureXmlCommentsFile("swagger-enabled.xml");

        var services = new ServiceCollection();
        services.AddOptions();

        var options = new BlocksSwaggerOptions
        {
            Version = "v7",
            Title = "Auth API",
            Description = "Auth enabled test",
            XmlCommentsFilePath = "swagger-enabled.xml",
            EnableBearerAuth = true,
            ServiceName = "orders"
        };

        services.AddBlocksSwagger(options);

        using var provider = services.BuildServiceProvider();
        var swaggerOptions = provider.GetRequiredService<IOptions<SwaggerGenOptions>>().Value;

        Assert.Contains("v7", swaggerOptions.SwaggerGeneratorOptions.SwaggerDocs.Keys);
        Assert.Contains(BlocksConstants.BlocksKey, swaggerOptions.SwaggerGeneratorOptions.SecuritySchemes.Keys);
        Assert.Contains(JwtBearerDefaults.AuthenticationScheme, swaggerOptions.SwaggerGeneratorOptions.SecuritySchemes.Keys);
        Assert.Contains(
            swaggerOptions.DocumentFilterDescriptors,
            d => d.Type == typeof(AddServiceVersionToPathsFilter));
    }

    [Fact]
    public void AddBlocksSwagger_ShouldUseDefaultXmlFileName_WhenPathNotProvided()
    {
        var defaultXmlFileName = "Blocks.Genesis.xml";
        EnsureXmlCommentsFile(defaultXmlFileName);

        var services = new ServiceCollection();
        services.AddOptions();

        var options = new BlocksSwaggerOptions
        {
            Version = "v8",
            Title = "Default XML API",
            Description = "Default xml path branch",
            XmlCommentsFilePath = string.Empty,
            EnableBearerAuth = false,
            ServiceName = string.Empty
        };

        var ex = Record.Exception(() => services.AddBlocksSwagger(options));

        Assert.Null(ex);

        using var provider = services.BuildServiceProvider();
        var swaggerOptions = provider.GetRequiredService<IOptions<SwaggerGenOptions>>().Value;
        Assert.Contains("v8", swaggerOptions.SwaggerGeneratorOptions.SwaggerDocs.Keys);
    }

    private static void EnsureXmlCommentsFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
                File.WriteAllText(path,
                        "<?xml version=\"1.0\"?><doc><assembly><name>XUnitTest</name></assembly><members></members></doc>");
    }
}
