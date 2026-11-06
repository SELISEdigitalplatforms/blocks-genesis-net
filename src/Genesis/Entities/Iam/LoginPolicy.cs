using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

/// <summary>
/// Where and how a tenant's users may sign in, stored on <see cref="TenantConfiguration.LoginPolicy"/>.
/// Every switch defaults to on, and a document written before a switch existed reads it as on, so
/// nothing is blocked until an admin turns something off.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TenantLoginPolicy
{
    /// <summary>The app's own login form (IAM <c>/auth/*</c>).</summary>
    public bool AllowEmbedded { get; set; } = true;

    /// <summary>The IAM login page (IAM <c>/oidc/*</c>).</summary>
    public bool AllowOidc { get; set; } = true;

    public bool AllowPassword { get; set; } = true;

    public bool AllowSocial { get; set; } = true;

    /// <summary>True when at least one place and one method stay on; a policy with none would lock everyone out.</summary>
    public bool AllowsAnySignIn() => (AllowEmbedded || AllowOidc) && (AllowPassword || AllowSocial);
}

/// <summary>Values of <see cref="OidcClientLoginPolicy.Method"/>.</summary>
public static class OidcClientLoginMethods
{
    /// <summary>Follow the tenant: password and/or social, as the tenant allows.</summary>
    public const string Default = "default";
    public const string Password = "password";
    public const string Social = "social";

    public static bool IsKnown(string? value) => value is Default or Password or Social;
}

/// <summary>Values of <see cref="OidcClientLoginPolicy.Providers"/>.</summary>
public static class OidcClientProviderModes
{
    /// <summary>Every enabled social provider, including ones added later.</summary>
    public const string Default = "default";

    /// <summary>Only the providers in <see cref="OidcClientLoginPolicy.SelectedProviderIds"/>.</summary>
    public const string Selected = "selected";

    public static bool IsKnown(string? value) => value is Default or Selected;
}

/// <summary>
/// How one OIDC client narrows the tenant's sign-in policy, stored on
/// <see cref="OidcClientRegistration.LoginPolicy"/>. Missing = Default method and Default providers.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class OidcClientLoginPolicy
{
    public string Method { get; set; } = OidcClientLoginMethods.Default;

    public string Providers { get; set; } = OidcClientProviderModes.Default;

    /// <summary><see cref="IdentityProvider"/> ItemIds; read only when <see cref="Providers"/> is Selected.</summary>
    public List<string> SelectedProviderIds { get; set; } = new();
}
