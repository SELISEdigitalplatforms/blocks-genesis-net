namespace Blocks.Genesis
{
    public class JwtTokenParameters
    {
        public string Issuer { get; init; }
        public List<string> Audiences { get; init; }
        public string PublicCertificatePath { get; init; }
        public string PublicCertificatePassword { get; init; }
        public string PrivateCertificatePassword { get; init; }
        public string Subject { get; init; }
        public int CertificateValidForNumberOfDays { get; init; }
        public DateTime IssueDate { get; init; }
        public int AccessTokenValidForNumberMinute { get; init; } = 7;
        public int RefreshTokenValidForNumberMinute { get; init; } = 30;
        public int RememberMeRefreshTokenValidForNumberMinute { get; init; } = 30 * 60 * 24;

    }

}
