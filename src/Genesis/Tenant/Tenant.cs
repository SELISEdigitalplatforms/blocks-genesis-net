namespace Blocks.Genesis
{
    public class Tenant
    {
        public string TenantId { get; set; }
        public string ApplicationDomain { get; set; }
        public string DbConnectionString { get; set; }
        public JwtTokenParameters JwtTokenParameters { get; set; }
    }
}