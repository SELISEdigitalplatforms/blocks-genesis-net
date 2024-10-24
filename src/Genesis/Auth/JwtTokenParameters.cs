namespace Blocks.Genesis
{
    public record JwtTokenParameters
    {
        public string Issuer { get; init; }
        public List<string> Audiences { get; init; }
        public string SigningKeyPath { get; init; }
        public string SigningKeyPassword { get; init; }
        public string PrivateCertificatePassword { get; init; }
        public string PublicCertificatePassword { get; init; }
        public string Subject { get; init; }
        public int ValidForNumberOfDays { get; init; }
        public DateTime IssueDate { get; init; }

    }

}
