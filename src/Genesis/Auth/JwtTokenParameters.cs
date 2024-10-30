namespace Blocks.Genesis
{
    public class JwtTokenParameters
    {
        public string Issuer { get; init; }
        public List<string> Audiences { get; init; }
        public string PublicCertificatePath { get; init; }
        public string PublicCertificatePassword { get; init; }
        public string PrivateCertificatePassword { get; init; }
        public int CertificateValidForNumberOfDays { get; init; }
        public DateTime IssueDate { get; init; }
        public int AccessTokenValidForNumberMinutes { get; init; } = 7;
        public int RefreshTokenValidForNumberMinutes { get; init; } = 30;
        public int RememberMeRefreshTokenValidForNumberMinutes { get; init; } = 30 * 60 * 24;
        public int GetNumberOfWrongAttemptsToLockTheAccount { get; set; }
        public int AccountLockDurationInMinutes { get; set; }

    }

}
