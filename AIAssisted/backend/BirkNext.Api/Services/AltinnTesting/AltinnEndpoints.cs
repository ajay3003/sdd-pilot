using System.Text.RegularExpressions;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// The Altinn host addresses per logical environment (<c>AltinnTesting:Endpoints</c>). Integration Quality Review stores only the logical
/// environment (Test); these addresses turn it into hosts, so an Altinn host change is a configuration change here and nothing else. Only Test
/// (TT02) is ever contacted; Production has no entry and is refused. Addresses only: never a secret.
/// </summary>
public sealed class AltinnEndpointOptions
{
    public AltinnEnvironmentEndpointOptions Test { get; set; } = new()
    {
        PlatformBaseUrl = AltinnEnvironmentEndpointOptions.Tt02PlatformBaseUrl, AppsDomain = AltinnEnvironmentEndpointOptions.Tt02AppsDomain,
    };
}

/// <summary>One Altinn environment: the platform base (authentication exchange, Storage, Resource Registry) and the apps domain under which
/// each app is served as <c>https://{org}.{AppsDomain}/{org}/{app}/</c>.</summary>
public sealed class AltinnEnvironmentEndpointOptions
{
    public const string Tt02PlatformBaseUrl = "https://platform.tt02.altinn.no/";
    public const string Tt02AppsDomain = "apps.tt02.altinn.no";

    public string? PlatformBaseUrl { get; set; }
    public string? AppsDomain { get; set; }
}

/// <summary>
/// The validated Altinn addresses of one environment and every URL built from them. All Altinn consumers (public metadata, policy read, the
/// token exchange, Storage reads, the app API of a real run, and the request guard's allowlist) take their hosts from here. A configured
/// address is not a verified one: nothing here contacts it.
/// </summary>
public sealed partial class AltinnEndpoints
{
    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    private AltinnEndpoints(Uri platform, string appsDomain)
    {
        Platform = platform;
        AppsDomain = appsDomain;
    }

    /// <summary>Platform base address, ending in a slash.</summary>
    public Uri Platform { get; }
    /// <summary>The domain apps are served under; an app's host is <c>{org}.{AppsDomain}</c>.</summary>
    public string AppsDomain { get; }

    public Uri TokenExchange => new(Platform, "authentication/api/v1/exchange/id-porten");
    public Uri StorageInstances => new(Platform, "storage/api/v1/instances/");

    /// <summary>The base address of an app, built only from a validated org/app (never request input).</summary>
    public Uri AppBase(string org, string app) => new($"https://{org}.{AppsDomain}/{org}/{app}/");

    /// <summary>The Storage read of one instance, built only from the configured base, a positive numeric party id and a GUID.</summary>
    public Uri StorageInstance(long instanceOwnerPartyId, Guid instanceGuid) =>
        instanceOwnerPartyId > 0 ? new Uri(StorageInstances, $"{instanceOwnerPartyId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{instanceGuid:D}")
            : throw new ArgumentOutOfRangeException(nameof(instanceOwnerPartyId));

    /// <summary>The Resource Registry address of an app's authorization policy (anonymous, read-only).</summary>
    public Uri PolicySource(string org, string app) => new(Platform, $"resourceregistry/api/v1/resource/app_{org}_{app}/policy");

    /// <summary>Whether the host is this environment's platform host or one app host <c>{org}.{AppsDomain}</c> (a single, valid org label).</summary>
    public bool IsAltinnHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host == Platform.Host) return true;
        var suffix = "." + AppsDomain;
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var org = host[..^suffix.Length];
        return org.Length > 0 && !org.Contains('.', StringComparison.Ordinal) && DomainPattern().IsMatch(org + ".x");
    }

    /// <summary>
    /// Validates one environment's configured addresses: an absolute https platform base on the default port without credentials, query or
    /// fragment, a plain domain for apps, and neither a production Altinn host. A problem is reported by its configuration key, never guessed.
    /// </summary>
    public static AltinnEndpoints Create(AltinnEnvironmentEndpointOptions? options, string key, Func<string, bool> isProductionHost)
    {
        var platformKey = $"{AltinnTestingOptions.Section}:Endpoints:{key}:PlatformBaseUrl";
        var appsKey = $"{AltinnTestingOptions.Section}:Endpoints:{key}:AppsDomain";
        if (string.IsNullOrWhiteSpace(options?.PlatformBaseUrl)) throw new AltinnContractException($"No Altinn platform address is configured ({platformKey}).");
        if (!Uri.TryCreate(options.PlatformBaseUrl.Trim(), UriKind.Absolute, out var platform) || platform.Scheme != Uri.UriSchemeHttps || !platform.IsDefaultPort
            || !string.IsNullOrEmpty(platform.UserInfo) || !string.IsNullOrEmpty(platform.Query) || !string.IsNullOrEmpty(platform.Fragment))
            throw new AltinnContractException($"The Altinn platform address ({platformKey}) must be an absolute https address on the default port, without credentials, query or fragment.");
        var apps = options.AppsDomain?.Trim().Trim('.').ToLowerInvariant() ?? "";
        if (apps.Length == 0) throw new AltinnContractException($"No Altinn apps domain is configured ({appsKey}).");
        if (!DomainPattern().IsMatch(apps)) throw new AltinnContractException($"The Altinn apps domain ({appsKey}) must be a plain domain name such as {AltinnEnvironmentEndpointOptions.Tt02AppsDomain}.");
        if (isProductionHost(platform.Host.ToLowerInvariant()) || isProductionHost("org." + apps))
            throw new AltinnContractException($"The configured Altinn addresses for {key} point at Production, which is never contacted.");
        var path = platform.AbsolutePath.EndsWith('/') ? platform.AbsolutePath : platform.AbsolutePath + "/";
        return new AltinnEndpoints(new UriBuilder(platform) { Path = path }.Uri, apps);
    }
}
