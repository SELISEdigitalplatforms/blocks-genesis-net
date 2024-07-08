namespace Blocks.Genesis
{
    public class JwtTokenParameters
    { 
        public required string Issuer { get; set; }
        public required IEnumerable<string> Audiences { get; set; }
        public required string SigningKeyPath { get; set; }
        public required string SigningKeyPassword { get; set; }
    }

}
