namespace Blocks.Genesis
{
    public interface IJwtValidationService
    {
        public JwtTokenParameters GetTokenParameters(string tenantId);
        void SaveTokenParameters(string tenantId, JwtTokenParameters parameters);
    }

}