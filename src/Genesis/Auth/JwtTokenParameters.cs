namespace Blocks.Genesis
{
    public record JwtTokenParameters
    {
        public string Issuer { get; init; }
        public List<string> Audiences { get; init; }
        public string SigningKeyPath { get; init; }
        public string SigningKeyPassword { get; init; }
    }

}
