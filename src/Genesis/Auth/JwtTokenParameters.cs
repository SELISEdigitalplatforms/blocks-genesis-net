namespace Blocks.Genesis
{
    public class JwtTokenParameters
    {
        public string Issuer { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public List<string> Audiences { get; set; } = [];
        public string PublicCertificatePath { get; set; } = string.Empty;
        public string PublicCertificatePassword { get; set; } = string.Empty;
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
