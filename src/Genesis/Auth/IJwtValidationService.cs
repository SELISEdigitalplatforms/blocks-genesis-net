namespace Blocks.Genesis
{
    public interface IJwtValidationService
    {
        Task<JwtValidationParameters> GetValidationParametersAsync(string issuer, string audience);
    }

}
