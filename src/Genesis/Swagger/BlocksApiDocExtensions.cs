using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;

namespace Blocks.Genesis
{
    public static class BlocksApiDocExtensions
    {
        public static void AddBlocksSwagger(this IServiceCollection services, BlocksSwaggerOptions blocksSwaggerOptions)
        {
            if (blocksSwaggerOptions == null) return;

            services.AddSwaggerGen(options =>
            {
                var openApiInfo = new OpenApiInfo
                {
                    Version = blocksSwaggerOptions.Version,
                    Title = blocksSwaggerOptions.Title,
                    Description = blocksSwaggerOptions.Description
                };

                options.SwaggerDoc(blocksSwaggerOptions.Version, openApiInfo);

                // Use fully qualified type names to avoid collisions when DTO names repeat across namespaces.
                options.CustomSchemaIds(type => type.FullName?.Replace("+", ".") ?? type.Name);

                var xmlFilename = string.IsNullOrWhiteSpace(blocksSwaggerOptions.XmlCommentsFilePath)
                    ? $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"
                    : blocksSwaggerOptions.XmlCommentsFilePath;

                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));

                EnableAuthorization(options, blocksSwaggerOptions.EnableBearerAuth);
                AddCustomHeader(options, BlocksConstants.BlocksKey, "API key needed to access the endpoints.");
            });

        }

        private static void EnableAuthorization(SwaggerGenOptions options, bool isEnable)
        {
            if (!isEnable) return;

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = "JWT Authentication",
                Description = "Enter 'Bearer' [space] and then your valid token",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            };

            var securitySchemeReference = new OpenApiSecuritySchemeReference(
                JwtBearerDefaults.AuthenticationScheme,
                hostDocument: null,
                externalResource: null);

            options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, securityScheme);
            options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
            {
                { securitySchemeReference, new List<string>() }
            });
        }

        private static void AddCustomHeader(SwaggerGenOptions options, string headerName, string description)
        {
            var securityScheme = new OpenApiSecurityScheme
            {
                Name = headerName,
                Description = description,
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = headerName
            };

            var securitySchemeReference = new OpenApiSecuritySchemeReference(
                headerName,
                hostDocument: null,
                externalResource: null);

            options.AddSecurityDefinition(headerName, securityScheme);
            options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
            {
                { securitySchemeReference, new List<string>() }
            });
        }
    }
}
