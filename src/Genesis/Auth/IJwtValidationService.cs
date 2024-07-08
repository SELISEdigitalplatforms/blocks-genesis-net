using Microsoft.IdentityModel.Tokens;

namespace Blocks.Genesis
{
    public interface IJwtValidationService
    {
        public IEnumerable<string> GetAudiences();
        public IEnumerable<string> GetIssuers();
        public IEnumerable<X509SecurityKey> GetSecurityKeys();
    }

}
