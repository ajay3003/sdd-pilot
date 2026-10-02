namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>
/// Azure Environment Analysis settings (section <c>AzureEnvironment</c>). Off unless Enabled is true and a ClientId is configured: the
/// ClientId is a PUBLIC client (Entra app registration, "Mobile and desktop applications" platform, redirect URI <c>http://localhost</c>,
/// delegated "Azure Service Management / user_impersonation"). No client secret exists or is accepted; the person signs in interactively
/// (MFA, Conditional Access, PIM-activated roles are the person's own) and BirkNext acts with exactly their permissions — read-only.
/// </summary>
public sealed class AzureEnvironmentOptions
{
    public const string SectionName = "AzureEnvironment";
    public const string ManagementEndpoint = "https://management.azure.com";
    public const string ManagementScope = "https://management.azure.com/.default";
    public const string AuthorityHost = "https://login.microsoftonline.com";

    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Directory (tenant) id or domain. Default "organizations" (the account's home tenant).</summary>
    public string? TenantId { get; set; }
    /// <summary>Device-code sign-in as the fallback when the dedicated Edge profile cannot be started (e.g. a remote BirkNext runtime).</summary>
    public bool AllowDeviceCode { get; set; } = true;
    /// <summary>Overrides the dedicated sign-in profile directory (must not be the normal Edge profile or another BirkNext profile).</summary>
    public string? ProfileDirectory { get; set; }
    public int MaxResources { get; set; } = 5000;
    public int MaxDeepReads { get; set; } = 400;
    public int MaxSubscriptions { get; set; } = 20;

    public bool Configured => Enabled && !string.IsNullOrWhiteSpace(ClientId);
    public string Tenant => string.IsNullOrWhiteSpace(TenantId) ? "organizations" : TenantId.Trim();

    public static IReadOnlyList<string> Requirements() =>
    [
        "Set AzureEnvironment:Enabled to true in the BirkNext.Api configuration.",
        "Set AzureEnvironment:ClientId to an Entra app registration that is a public client (platform \"Mobile and desktop applications\", redirect URI http://localhost).",
        "Grant the app registration the delegated permission Azure Service Management → user_impersonation (an administrator may need to consent).",
        "Optionally set AzureEnvironment:TenantId to the directory to sign in to (default: your home tenant).",
        "No client secret, certificate, username or password is used or stored. You sign in yourself, with MFA, and BirkNext uses only your own read access.",
    ];
}
