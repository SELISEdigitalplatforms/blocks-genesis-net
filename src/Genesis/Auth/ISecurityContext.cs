namespace Blocks.Genesis
{
    public interface ISecurityContext
    {
        IEnumerable<string> Roles { get; }
        string TenantId { get; }
        string OauthBearerToken { get; }
        string UserId { get; }
        IEnumerable<string> Audiances { get; }
        Uri RequestUri { get; }
        string OrganizationId { get; }
        bool IsAuthenticated { get; }
    }
}
