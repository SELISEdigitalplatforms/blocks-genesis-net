namespace Blocks.Genesis
{
    public class JwtTokenParameters
    {
        public required string Issuer { get; init; }
        public string Subject { get; init; }
        public required List<string> Audiences { get; set; }
        public string PublicCertificatePath { get; set; }
        public required string PublicCertificatePassword { get; init; }
        public required string PrivateCertificatePassword { get; set; }
        public int CertificateValidForNumberOfDays { get; init; } = 365;
        public required DateTime IssueDate { get; init; }
    }

}
