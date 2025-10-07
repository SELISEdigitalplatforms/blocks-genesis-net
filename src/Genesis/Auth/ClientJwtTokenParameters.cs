
namespace Blocks.Genesis
{
    public class ClientJwtTokenParameters
    {
        public string Issuer { get; set; }
        public string Subject { get; set; }
        public List<string> Audiences { get; set; }
        public string PublicCertificatePath { get; set; }
        public string PublicCertificatePassword { get; set; }
        public string CookieKey  { get; set; }
    }
}
