
namespace Blocks.Genesis
{
    public class ThirdPartyJwtTokenParameters
    {
        public string Issuer { get; set; }
        public string Subject { get; set; }
        public List<string> Audiences { get; set; }
        public string PublicCertificatePath { get; set; }
        public string JwksUrl { get; set; }
        public string PublicCertificatePassword { get; set; }
        public string CookieKey  { get; set; }
    }
}
