using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography.X509Certificates;

namespace Blocks.Genesis
{
    public class JwtValidationService : IJwtValidationService
    {
        private readonly List<JwtTokenParameters> _tokenParameters;

        public JwtValidationService()
        {
            _tokenParameters = GetValidationParameters();
        }

        public IEnumerable<string> GetAudiences()
        {
            var audiences = new HashSet<string>();
            foreach (var token in _tokenParameters)
            {
                audiences.UnionWith(token.Audiences);
            }
            return audiences;
        }

        public IEnumerable<string> GetIssuers()
        {
            return _tokenParameters.Select(x => x.Issuer).Distinct();
        }

        public IEnumerable<X509SecurityKey> GetSecurityKeys()
        {
            foreach (var param in _tokenParameters)
            {
                var certificate = CreateSecurityKey(param);
                if (certificate != null)
                {
                    yield return new X509SecurityKey(certificate);
                }
            }
        }

        private List<JwtTokenParameters> GetValidationParameters()
        {
            return new List<JwtTokenParameters>
            {
                new JwtTokenParameters
                {
                    Issuer = "https://issuer1.com",
                    Audiences = new[] {"audience1" },
                    SigningKeyPassword = "signingKey1",
                    SigningKeyPath = ""
                }
            };
        }

        private static X509Certificate2 CreateSecurityKey(JwtTokenParameters validationParameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(validationParameters.SigningKeyPassword))
                {
                    return new X509Certificate2(validationParameters.SigningKeyPath);
                }
                else
                {
                    return new X509Certificate2(validationParameters.SigningKeyPath, validationParameters.SigningKeyPassword);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating security key: {e.Message}");
                return null;
            }
        }
    }
}
