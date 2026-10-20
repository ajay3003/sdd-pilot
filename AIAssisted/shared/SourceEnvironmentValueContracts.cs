using System.Text.Json.Serialization;

namespace BirkNext.SourceDomains;

// ── Environment-specific values declared in source ──────────────────────────────────────────────────────────────────
// A value a source artifact declares for ONE environment (appsettings.Qa.json, skole-dev.yml …), offered to a setup form of the target
// environment of the same kind. Declared in source ≠ configured ≠ deployed ≠ reachable ≠ runtime verified. A candidate is never saved by being
// shown: the form's own Save persists it. Generic: semantic keys name concepts, never a product, project or host.

/// <summary>The semantic keys environment-specific candidates are produced for (by exact configuration-key shape, never by free text).</summary>
public static class EnvironmentValueKeys
{
    /// <summary>The browser origin of the application's frontend (a CORS allowed origin or a non-loopback sign-in redirect origin).</summary>
    public const string FrontendOrigin = "Application.FrontendOrigin";
    /// <summary>The API base URL a frontend calls (a key named ApiBaseUrl).</summary>
    public const string ApiBaseUrl = "Application.ApiBaseUrl";
    /// <summary>The sign-in authority (a key named Authority with a URL value).</summary>
    public const string AuthenticationAuthority = "Authentication.Authority";
    /// <summary>The directory tenant id, read from the authority's path.</summary>
    public const string AuthenticationTenant = "Authentication.Tenant";
    /// <summary>A Service Bus namespace host (a ServiceBus …FQDN / FullyQualifiedNamespace key).</summary>
    public const string ServiceBusNamespace = "Messaging.ServiceBus.Namespace";
    /// <summary>An Event Hub name (an entity name under an EventHub key).</summary>
    public const string EventHubName = "Messaging.EventHub.Name";

    public static string Label(string key) => key switch
    {
        FrontendOrigin => "Frontend URL",
        ApiBaseUrl => "API base URL",
        AuthenticationAuthority => "Sign-in authority",
        AuthenticationTenant => "Tenant",
        ServiceBusNamespace => "Service Bus namespace",
        EventHubName => "Event Hub name",
        _ => key,
    };
}

/// <summary>One environment-specific value declared in source, with its provenance. Safe values only: a sensitive, secret, connection-string,
/// placeholder or loopback value is never a candidate.</summary>
public sealed record EnvironmentValueCandidate
{
    public string SemanticKey { get; init; } = "";
    public string Value { get; init; } = "";
    public SourceEnvironmentLabel Environment { get; init; } = SourceEnvironmentLabel.Default;
    /// <summary>The application/component the declaring file belongs to, when the source states it.</summary>
    public string? Component { get; init; }
    public Guid SnapshotId { get; init; }
    public string ArchiveFileName { get; init; } = "";
    /// <summary>The artifact inside the snapshot (appsettings.Qa.json …).</summary>
    public string File { get; init; } = "";
    public int Line { get; init; }
    /// <summary>The configuration path in the artifact (Cors:AllowedOrigins:0 …).</summary>
    public string ConfigurationKey { get; init; } = "";
    /// <summary>"Configuration" for configuration files.</summary>
    public string EvidenceKind { get; init; } = "Configuration";
    /// <summary>How the key maps to the semantic key ("CORS allowed origin", "authority path") — an interpretation, stated.</summary>
    public string Basis { get; init; } = "";
    /// <summary>How the environment was decided (the file-name suffix …) — a source label, not a deployed environment.</summary>
    public string EnvironmentBasis { get; init; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentValueState
{
    /// <summary>No saved value and no source value for this environment: empty field with guidance.</summary>
    NoValue,
    /// <summary>No saved value; exactly one value declared for this environment: offered as the field's value (not saved until Save).</summary>
    SourceSuggested,
    /// <summary>No saved value; several different values declared for this environment: nothing is offered as the value.</summary>
    ReviewRequired,
    /// <summary>A saved value with no source value to compare.</summary>
    Configured,
    /// <summary>A saved value that equals a source value.</summary>
    ConfiguredMatchesSource,
    /// <summary>A saved value that differs from the source: the saved value is kept.</summary>
    ConfiguredDiffersFromSource,
}

/// <summary>What a field shows for one target environment. <see cref="Value"/> is the saved value or the single source value; never a value of
/// another environment, a Local or Default value, or one picked among conflicting values.</summary>
public sealed record EnvironmentValueResolution(string SemanticKey, EnvironmentValueState State, string? Value, IReadOnlyList<EnvironmentValueCandidate> Candidates)
{
    public SourceEnvironmentKind? Environment { get; init; }
    public string? ConfiguredValue { get; init; }
    /// <summary>Always "Runtime not verified": declared in source says nothing about the deployed system.</summary>
    public string RuntimeState { get; init; } = "Runtime not verified";
    public bool HasSourceValue => Candidates.Count > 0;
}

/// <summary>The workspace's environment-specific source values (every Source Analysis snapshot), plus which environments each snapshot declares.</summary>
public sealed record SourceEnvironmentValuesView
{
    public IReadOnlyList<EnvironmentValueCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Precedence, environment matching and conflict rules for environment-specific values. Pure; shared by backend and frontend.</summary>
public static class EnvironmentValueResolver
{
    /// <summary>The source environment a target environment matches: its explicit type, or (for a custom/RC type) an environment its name states.
    /// Local and Default are never a target environment's fallback; an unknown environment matches nothing.</summary>
    public static SourceEnvironmentKind? TargetKind(string? environmentType, string? environmentName)
    {
        var fromType = environmentType?.Trim().ToLowerInvariant() switch
        {
            "development" => SourceEnvironmentKind.Development,
            "qa" => SourceEnvironmentKind.QA,
            "test" => SourceEnvironmentKind.Test,
            "production" => SourceEnvironmentKind.Production,
            "local" => SourceEnvironmentKind.Local,
            _ => (SourceEnvironmentKind?)null,
        };
        return fromType ?? (SourceEnvironments.FromName(environmentName) is { Kind: not SourceEnvironmentKind.Local } named ? named.Kind : null);
    }

    /// <summary>
    /// Saved value first (it always wins and is never replaced); otherwise the single value declared for exactly <paramref name="target"/>;
    /// several different values are Review required. <paramref name="component"/> narrows candidates to one component when the field belongs
    /// to one; values of different components are only compared when the field is one value for the whole target.
    /// </summary>
    public static EnvironmentValueResolution Resolve(string semanticKey, string? configured, SourceEnvironmentKind? target, IEnumerable<EnvironmentValueCandidate> all,
        string? component = null)
    {
        var candidates = target is not { } kind || kind is SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom ? [] :
            all.Where(c => c.SemanticKey == semanticKey && c.Environment.Kind == kind
                    && (component is null || string.Equals(c.Component, component, StringComparison.OrdinalIgnoreCase)))
               .OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.ArchiveFileName, StringComparer.Ordinal).ThenBy(c => c.File, StringComparer.Ordinal)
               .ToList();
        var distinct = candidates.Select(c => Normalize(c.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var state = candidates.Count == 0 ? EnvironmentValueState.Configured
                : distinct.Contains(Normalize(configured), StringComparer.OrdinalIgnoreCase) ? EnvironmentValueState.ConfiguredMatchesSource
                : EnvironmentValueState.ConfiguredDiffersFromSource;
            return new(semanticKey, state, configured, candidates) { Environment = target, ConfiguredValue = configured };
        }
        return distinct.Count switch
        {
            0 => new(semanticKey, EnvironmentValueState.NoValue, null, candidates) { Environment = target },
            1 => new(semanticKey, EnvironmentValueState.SourceSuggested, candidates[0].Value, candidates) { Environment = target },
            _ => new(semanticKey, EnvironmentValueState.ReviewRequired, null, candidates) { Environment = target },
        };
    }

    /// <summary>Comparison form of a value: a URL without its trailing slash. Display keeps the declared value.</summary>
    public static string Normalize(string value)
    {
        var v = value.Trim();
        return Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? v.TrimEnd('/') : v;
    }

    /// <summary>A host only a developer machine reaches: never a deployed environment's value.</summary>
    public static bool IsLoopback(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Host.ToLowerInvariant() is var host
        && (host is "localhost" or "127.0.0.1" or "[::1]" or "::1" or "0.0.0.0" or "host.docker.internal" || host.EndsWith(".localhost", StringComparison.Ordinal));
}

/// <summary>
/// Environment-specific values declared in configuration files (appsettings.{Env}.json, environment-named YAML …), as candidates for setup forms.
/// A key maps to a semantic key only by its exact shape (its last named segment, and for messaging the section it sits in) — never by searching
/// values for a product or host name. Only safe values are kept: a sensitive, secret, connection-string, placeholder or empty value never
/// becomes a candidate, and a loopback URL (a developer machine) is never a deployed environment's value.
/// </summary>
public static class SourceEnvironmentValueExtractor
{
    private static readonly HashSet<ConfigurationValueKind> UnsafeKinds =
        [ConfigurationValueKind.Secret, ConfigurationValueKind.SecretReference, ConfigurationValueKind.ConnectionString, ConfigurationValueKind.Placeholder, ConfigurationValueKind.Empty];

    public static IReadOnlyList<EnvironmentValueCandidate> Candidates(IEnumerable<BirkNext.Integrations.IqrSourceSnapshot> snapshots) =>
        snapshots.SelectMany(snapshot => Candidates(snapshot.EvidenceDomains, snapshot.Id, snapshot.Archive.FileName)).ToList();

    public static IEnumerable<EnvironmentValueCandidate> Candidates(SourceEvidenceDomainsSnapshot? domains, Guid snapshotId, string archiveFileName)
    {
        var configuration = domains?.Configuration;
        if (configuration is null) yield break;
        var files = configuration.Files.GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var entry in configuration.Entries)
        {
            if (entry.Sensitivity != ConfigurationSensitivity.None || UnsafeKinds.Contains(entry.ValueKind)) continue;
            var value = entry.ValuePreviewSafe?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Contains("${", StringComparison.Ordinal) || value.Contains("#{", StringComparison.Ordinal)) continue;
            foreach (var (key, mapped, basis) in Map(entry, value))
            {
                if (EnvironmentValueResolver.IsLoopback(mapped)) continue;
                yield return new EnvironmentValueCandidate
                {
                    SemanticKey = key, Value = mapped, Environment = entry.Environment, Component = entry.Component, SnapshotId = snapshotId,
                    ArchiveFileName = archiveFileName, File = entry.File, Line = entry.Line, ConfigurationKey = entry.Key, Basis = basis,
                    EnvironmentBasis = files.TryGetValue(entry.File, out var file) ? file.EnvironmentBasis : "none",
                };
            }
        }
    }

    /// <summary>The configuration environments a snapshot declares (Development, QA, Local …), Default excluded. Informational: a source label is
    /// not a deployed environment.</summary>
    public static IReadOnlyList<string> Environments(BirkNext.Integrations.IqrSourceSnapshot snapshot) =>
        (snapshot.EvidenceDomains?.Configuration.Files ?? []).Select(f => f.Environment.Kind)
            .Where(kind => kind is not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom)).Distinct().Order().Select(kind => kind.ToString()).ToList();

    private static IEnumerable<(string Key, string Value, string Basis)> Map(ConfigurationEntry entry, string value)
    {
        var segments = entry.NormalizedKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
        // The last NAMED segment: array items (allowedorigins:0) are named by their array.
        var name = segments.LastOrDefault(s => !s.All(char.IsDigit)) ?? "";
        var isUrl = entry.ValueKind == ConfigurationValueKind.Url && Uri.TryCreate(value, UriKind.Absolute, out _);
        switch (name)
        {
            case "allowedorigins" when isUrl:
                yield return (EnvironmentValueKeys.FrontendOrigin, Origin(value), "CORS allowed origin (the browser origin allowed to call this component)");
                yield break;
            case "redirecturi" or "postlogoutredirecturi" when isUrl:
                yield return (EnvironmentValueKeys.FrontendOrigin, Origin(value), "sign-in redirect URI origin");
                yield break;
            case "apibaseurl" when isUrl:
                yield return (EnvironmentValueKeys.ApiBaseUrl, value, "API base URL the component calls");
                yield break;
            case "authority" when isUrl:
                yield return (EnvironmentValueKeys.AuthenticationAuthority, value.TrimEnd('/'), "sign-in authority");
                if (TenantOf(value) is { } tenant) yield return (EnvironmentValueKeys.AuthenticationTenant, tenant, "tenant id in the authority path");
                yield break;
        }
        var inServiceBus = segments.Any(s => s.Contains("servicebus", StringComparison.Ordinal));
        if (inServiceBus && name is "fqdn" or "fullyqualifiednamespace" && value.Contains('.'))
            yield return (EnvironmentValueKeys.ServiceBusNamespace, isUrl ? new Uri(value).Host : value, "Service Bus namespace host");
        else if (segments.Any(s => s.Contains("eventhub", StringComparison.Ordinal)) && entry.ValueKind == ConfigurationValueKind.EntityName
                 && name is not ("consumergroup" or "fqdn" or "fullyqualifiednamespace" or "namespace"))
            yield return (EnvironmentValueKeys.EventHubName, value, "Event Hub entity name");
    }

    private static string Origin(string url) => new Uri(url).GetLeftPart(UriPartial.Authority);

    private static string? TenantOf(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.Segments.Select(s => s.Trim('/')).FirstOrDefault(s => s.Length > 0) is { } first
            && Guid.TryParse(first, out var tenant) ? tenant.ToString("D") : null;
}
