
namespace Blocks.Genesis
{
    public class ApplicationContext
    {
        public string Environment { get; set; } // e.g., DEV, STG, PROD
        public string Domain { get; set; } // e.g.,dev.example.com
        public string CookieDomain { get; set; }
    }
}
