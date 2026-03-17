namespace Blocks.Genesis
{
    public class JwtTokenParameters
    {
        public string Issuer { get; set; }
        public string Subject { get; set; }
        public List<string> Audiences { get; set; }
        public string PublicCertificatePath { get; set; }
        public string PublicCertificatePassword { get; set; }
        public required string PrivateCertificatePassword { get; set; }
        public CertificateStorageType CertificateStorageType { get; set; } = CertificateStorageType.Azure;
        public int CertificateValidForNumberOfDays { get; init; } = 365;
        public required DateTime IssueDate { get; set; }
    }

    public enum CertificateStorageType
    {
        Azure = 1,
        Filefilesystem = 2,
        Mongodb = 3
    }

}
