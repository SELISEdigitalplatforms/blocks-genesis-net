namespace Blocks.Genesis
{
    public class JwtTokenParameters
    {
        public required string Issuer { get; init; }
        public required List<string> Audiences { get; init; }
        public required string PublicCertificatePath { get; init; }
        public required string PublicCertificatePassword { get; init; }
        public required string PrivateCertificatePassword { get; init; }
        public int CertificateValidForNumberOfDays { get; init; } = 365;
        public required DateTime IssueDate { get; init; }
    }

}
