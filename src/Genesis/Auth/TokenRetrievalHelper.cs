using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;

namespace Blocks.Genesis
{
    public static class TokenRetrievalHelper
    {
        public static string GetToken(HttpRequest request)
        {
            var token = request.Headers["Authorization"];

            if (!token.Equals(StringValues.Empty))
            {
                return token.ToString().Substring(7);
            }

            return GetTokenFromCookie(request);
        }

        public static string GetTokenFromCookie(HttpRequest request)
        {
            if (!request.Cookies.Any())
            {
                return string.Empty;
            }

            var originHost = GetHostOfRequestOrigin(request);

            return request.Cookies[originHost];
        }

        public static string GetHostOfRequestOrigin(HttpRequest request)
        {
            var origin = request.Headers["Origin"];

            if (origin.Equals(StringValues.Empty))
            {
                origin = request.Headers["Referer"];
            }

            if (origin.Equals(StringValues.Empty))
            {
                return string.Empty;
            }

            return new Uri(origin).Host;
        }

        public static X509Certificate2 CreateSecurityKey(JwtValidationParameters validationParameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(validationParameters.SigningKeyPassword))
                {
                    var x509Certificate2 = new X509Certificate2(validationParameters.SigningKeyPath);
                    return x509Certificate2;
                }
                else
                {
                    var x509Certificate2 = new X509Certificate2(validationParameters.SigningKeyPath, validationParameters.SigningKeyPassword);
                    return x509Certificate2;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return null;
        }

        public static void HandleTokenIssuer(ClaimsIdentity claimsIdentity, string requestUri, string jwtBearerToken)
        {
            var requestClaims = new Claim[]
            {
            new Claim("RequestUri", requestUri),
            new Claim("OauthBearerToken", jwtBearerToken)
            };

            claimsIdentity.AddClaims(requestClaims);
        }
    }

}
