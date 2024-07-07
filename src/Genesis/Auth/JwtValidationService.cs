namespace Blocks.Genesis
{
    public class JwtValidationService : IJwtValidationService
    {

        public Task<JwtValidationParameters> GetValidationParametersAsync(string issuer, string audienceId)
        {
            var parameters = new List<JwtValidationParameters>
            {
                new JwtValidationParameters {
                    AudienceId = "audience1",
                   Issuer = "https://issuer1.com",
                   Audiences = new[] {"audience1" },
                   SigningKeyPassword = "signingKey1",
                   SigningKeyPath = issuer,
                }
            };
            var matchingParams = parameters?.FirstOrDefault(p => p.Issuer == issuer && p.AudienceId == audienceId);

            return Task.FromResult(matchingParams);
        }
    }

}
