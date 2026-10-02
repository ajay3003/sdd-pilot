using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SecurityExpectations;

namespace BirkNext.Api.Services.SecurityExpectations;

/// <summary>Executed only during source ingestion. Reuses architecture input/configuration and extracted auth/client facts.
/// Only public identifiers, explicit endpoint hosts, redirect/authority URLs and header names leave the in-memory workspace.</summary>
public static class SecurityExpectationSourceAnalyzer
{
    public const int Version = 1;
    public static SecuritySourceEvidence Analyze(IqrSourceSnapshot snapshot, IqrSourceArchiveReader.Workspace workspace, CancellationToken ct = default)
    {
        var input = ArchitectureInput.From(snapshot.Id, workspace);
        var facts = new AuthenticationArchitectureExtractor().Analyze(input, ct).Facts
            .Concat(new HttpClientArchitectureExtractor().Analyze(input, ct).Facts)
            .Concat(new GraphQlArchitectureExtractor().Analyze(input, ct).Facts).ToList();
        return Analyze(snapshot, input, facts, workspace.Limitations, ct);
    }
    internal static SecuritySourceEvidence Analyze(IqrSourceSnapshot snapshot, ArchitectureInput input, IReadOnlyList<ArchitectureFact> facts,
        IReadOnlyList<string> limitations, CancellationToken ct, SourceAnalysis.Evidence.SourceConfigurationModel? configuration = null)
    {
        var candidates = new List<SecurityExpectationCandidate>();
        var diagnostics = new List<string>();
        void Add(SecurityExpectationField field, string raw, string component, ArchitectureEvidence evidence, ArchitectureEvidenceState state = ArchitectureEvidenceState.Confirmed)
        {
            // Never retain invalid raw values or their contents in diagnostics.
            var normalized = SecurityExpectationValues.Normalize(field, raw);
            if (normalized is null) { diagnostics.Add($"{field}: explicit value is malformed, unresolved or credential-shaped; value not retained ({ArchitectureText.Safe(evidence.File)})."); return; }
            var value = field is SecurityExpectationField.BackendDomain or SecurityExpectationField.RestHost or SecurityExpectationField.GraphQlHost or SecurityExpectationField.CdnHost
                ? normalized : raw.Trim();
            var key = $"{field}|{component}|{evidence.File}|{evidence.Symbol}|{normalized}";
            candidates.Add(new() {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant(),
                FieldType = field, Value = value, NormalizedValue = normalized,
                CandidateState = state == ArchitectureEvidenceState.Inferred ? SecurityCandidateState.Suggested : SecurityCandidateState.Detected,
                EvidenceState = state, Confidence = state == ArchitectureEvidenceState.Confirmed ? "Explicit source configuration" : "Source role requires review",
                SourceSnapshotId = snapshot.Id, SourceComponent = component, SourceFile = ArchitectureText.Safe(evidence.File),
                SourceLine = evidence.Line, SourceSymbol = ArchitectureText.Safe(evidence.Symbol), EvidenceType = evidence.Kind.ToString(),
                SupportingEvidence = [new(snapshot.Id, snapshot.Archive.FileName + " / " + component, ArchitectureText.Safe(evidence.File), evidence.Line,
                    ArchitectureText.Safe(evidence.Symbol), evidence.Kind.ToString(), raw.Trim(), state == ArchitectureEvidenceState.Confirmed ? "Explicit source configuration" : "Source role requires review", state, snapshot.Archive.Sha256)],
                Explanation = evidence.Explanation, SuggestedAction = SecurityExpectationValues.Singleton(field) ? "Accept or explicitly replace" : "Add or ignore"
            });
            if (field == SecurityExpectationField.Authority && Uri.TryCreate(raw, UriKind.Absolute, out var authority) &&
                authority.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) &&
                authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } tenant &&
                SecurityExpectationValues.Normalize(SecurityExpectationField.TenantId, tenant) is not null)
                Add(SecurityExpectationField.TenantId, tenant, component, evidence with { Explanation = "Tenant explicitly identified by tenant-specific authority URL." });
        }
        foreach (var project in input.Projects.Where(p => !p.IsTest && !p.IsAspireHost))
        {
            ct.ThrowIfCancellationRequested();
            var component = snapshot.Architecture?.Components.FirstOrDefault(c => c.SourceProject == project.Path)?.Name ?? project.Name;
            var authFacts = facts.Where(f => f.ProjectPath == project.Path && f.Kind == "Auth").ToList();
            var endpointFacts = facts.Where(f => f.ProjectPath == project.Path && f.Kind is "HttpClient" or "GraphQlClient").ToList();
            bool EndpointKey(ArchitectureFact f, string key) => f["endpointConfigKey"]?.Equals(key, StringComparison.OrdinalIgnoreCase) == true ||
                (f["kind"] == "downstream" && f["configKey"] is { } section &&
                    (key.Equals(section + ":BaseUrl", StringComparison.OrdinalIgnoreCase) || key.Equals(section + ":BaseAddress", StringComparison.OrdinalIgnoreCase)));
            // Normalized Source Analysis configuration when available (appsettings exactly as Architecture parsed them, plus launchSettings, Compose,
            // .env and Functions settings owned by this project); otherwise the project's appsettings. Raw values are in memory only.
            foreach (var (configPath, configLine, key, raw) in ConfigurationValues(project, configuration))
            {
                if (SecurityExpectationValues.SecretShaped(key)) continue;
                var leaf = Regex.Replace(key, @":\d+$", "").Split(':').Last();
                var evidence = new ArchitectureEvidence(ArchitectureEvidenceKind.Configuration, configPath, configLine, key, "SecurityExpectations",
                    "Explicit configuration key in analyzed source. It does not establish deployed configuration or runtime behavior.");
                var identityContext = Regex.IsMatch(key, @"(?i)(^|:)(azuread|entra|authentication|auth|msal|oidc|jwt)(:|$)") ||
                    authFacts.Any(f => f["configKey"] is { } section && key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase));
                if (identityContext)
                {
                    if (leaf.Equals("Authority", StringComparison.OrdinalIgnoreCase)) Add(SecurityExpectationField.Authority, raw, component, evidence);
                    else if (leaf.Equals("TenantId", StringComparison.OrdinalIgnoreCase)) Add(SecurityExpectationField.TenantId, raw, component, evidence);
                    else if (leaf.Equals("ClientId", StringComparison.OrdinalIgnoreCase)) Add(SecurityExpectationField.ClientId, raw, component, evidence);
                    else if (Regex.IsMatch(leaf, @"(?i)^redirect(uri|url)s?$")) Add(SecurityExpectationField.RedirectUrl, raw, component, evidence);
                }
                if (endpointFacts.Any(f => f.Kind == "GraphQlClient" && EndpointKey(f, key)))
                    Add(SecurityExpectationField.GraphQlHost, raw, component, evidence, ArchitectureEvidenceState.StronglySupported);
                if (Regex.IsMatch(key, @"(?i)(graphql.*(endpoint|url|uri)|graphql:(endpoint|url|uri)|extensions:strawberryshake:url)$"))
                    Add(SecurityExpectationField.GraphQlHost, raw, component, evidence);
                else if (Regex.IsMatch(leaf, @"(?i)^(ApiBaseUrl|RestBaseUrl|BackendUrl|BackendBaseUrl|BackendDomain)$") ||
                    Regex.IsMatch(key, @"(?i)^ReverseProxy:Clusters:[^:]+:Destinations:[^:]+:Address$"))
                {
                    Add(SecurityExpectationField.BackendDomain, raw, component, evidence);
                    if (endpointFacts.Any(f => f.Kind == "GraphQlClient" && EndpointKey(f, key)))
                        Add(SecurityExpectationField.GraphQlHost, raw, component, evidence, ArchitectureEvidenceState.StronglySupported);
                    else if (Regex.IsMatch(leaf, @"(?i)^(ApiBaseUrl|RestBaseUrl)$") || key.StartsWith("ReverseProxy:", StringComparison.OrdinalIgnoreCase) ||
                        endpointFacts.Any(f => f.Kind == "HttpClient" && EndpointKey(f, key)))
                        Add(SecurityExpectationField.RestHost, raw, component, evidence, ArchitectureEvidenceState.StronglySupported);
                }
                else if (endpointFacts.Any(f => EndpointKey(f, key)))
                {
                    var graphql = endpointFacts.Any(f => f.Kind == "GraphQlClient" && EndpointKey(f, key));
                    Add(graphql ? SecurityExpectationField.GraphQlHost : SecurityExpectationField.RestHost, raw, component, evidence, ArchitectureEvidenceState.StronglySupported);
                    Add(SecurityExpectationField.BackendDomain, raw, component, evidence, ArchitectureEvidenceState.StronglySupported);
                }
                if (Regex.IsMatch(leaf, @"(?i)^(CdnBaseUrl|CdnUrl|StaticAssetOrigin|StaticAssetsBaseUrl)$"))
                    Add(SecurityExpectationField.CdnHost, raw, component, evidence);
                if (key.Split(':').Any(k => k.Equals("SecurityHeaders", StringComparison.OrdinalIgnoreCase)) &&
                    SecurityExpectationValues.Normalize(SecurityExpectationField.SecurityHeader, leaf) is not null)
                    Add(SecurityExpectationField.SecurityHeader, leaf, component, evidence with { Explanation = "Source-declared header configuration; header value is not retained and runtime presence is not assessed." });
            }
            foreach (var file in project.Code)
            {
                if (Regex.IsMatch(file.Text, @"\bUseHsts\s*\("))
                    Add(SecurityExpectationField.SecurityHeader, "Strict-Transport-Security", component,
                        new(ArchitectureEvidenceKind.ApplicationSource, file.Path, file.Line(Regex.Match(file.Text, @"\bUseHsts\s*\(").Index), "UseHsts", "SecurityHeaders",
                            "HSTS middleware declared in source; runtime header presence is not assessed."));
                // Literal identity settings are considered only in source with established authentication wiring.
                if (authFacts.Count > 0)
                foreach (Match match in Regex.Matches(file.Text, @"\b(Authority|TenantId|ClientId|RedirectUri|RedirectUrl)\s*=\s*""([^""]*)"""))
                {
                    var field = match.Groups[1].Value switch { "Authority" => SecurityExpectationField.Authority, "TenantId" => SecurityExpectationField.TenantId,
                        "ClientId" => SecurityExpectationField.ClientId, _ => SecurityExpectationField.RedirectUrl };
                    Add(field, match.Groups[2].Value, component, new(ArchitectureEvidenceKind.ApplicationSource, file.Path, file.Line(match.Index), match.Groups[1].Value,
                        "Authentication", "Explicit identity option in application source with authentication registration. No runtime verification."));
                }
                // Client facts carry exact registration provenance. GraphQL's additional endpoint
                // fact is consumed here only; it does not alter architecture target resolution.
                foreach (var fact in endpointFacts.Where(f => f.Evidence.File == file.Path && f["configuredEndpoint"] is not null))
                {
                    var field = fact.Kind == "GraphQlClient" ? SecurityExpectationField.GraphQlHost : SecurityExpectationField.RestHost;
                    var endpoint = fact["configuredEndpoint"]!;
                    Add(field, endpoint, component, fact.Evidence);
                    Add(SecurityExpectationField.BackendDomain, endpoint, component, fact.Evidence);
                }
                foreach (Match match in Regex.Matches(file.Text, @"\bHeaders\s*(?:\[\s*""([^""]+)""\s*\]|\.Add\(\s*""([^""]+)""|\.Append\(\s*""([^""]+)""|\.TryAdd\(\s*""([^""]+)"")"))
                {
                    var name = match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                    if (SecurityExpectationValues.Normalize(SecurityExpectationField.SecurityHeader, name) is not null)
                        Add(SecurityExpectationField.SecurityHeader, name, component, new(ArchitectureEvidenceKind.ApplicationSource, file.Path, file.Line(match.Index), "Headers",
                            "SecurityHeaders", "Source header assignment; header value is not stored and runtime presence is not assessed."));
                }
            }
        }
        foreach (var dependency in snapshot.Architecture?.Dependencies.Where(d => d.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl) ?? [])
        {
            var source = snapshot.Architecture!.Components.FirstOrDefault(c => c.Id == dependency.FromComponentId)?.Name ?? dependency.FromComponentId;
            // A component/technology ID is never a hostname. Only explicit absolute endpoint references qualify.
            if (dependency.TargetReference is { } reference && Uri.TryCreate(reference, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https")
            {
                var evidence = dependency.Evidence.FirstOrDefault();
                if (evidence is not null) Add(dependency.DependencyType == ArchitectureDependencyType.GraphQl ? SecurityExpectationField.GraphQlHost : SecurityExpectationField.RestHost,
                    reference, source, evidence, dependency.EvidenceState);
            }
            else if (!dependency.IsResolved) diagnostics.Add($"{source}: {dependency.DependencyType} endpoint is unresolved; no host promoted from technology or component name.");
        }
        return new() {
            SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256, AnalyzerVersion = Version,
            Candidates = candidates.DistinctBy(c => c.Id).ToList(), Diagnostics = diagnostics.Distinct().ToList(),
            UnsupportedEvidence = limitations.Where(l => l.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || l.Contains("Not analyzed", StringComparison.OrdinalIgnoreCase)).ToList()
        };
    }

    /// <summary>Application-configuration technologies whose keys Security Expectations reads (IaC and pipeline variables are not application settings).</summary>
    private static readonly HashSet<string> ApplicationConfiguration = new(StringComparer.Ordinal)
        { "ASP.NET Core appsettings", "Frontend appsettings", "GraphQL client configuration", "launchSettings", "Docker Compose", "Environment file", "Azure Functions settings" };

    private static IEnumerable<(string File, int Line, string Key, string Raw)> ConfigurationValues(ArchProject project, SourceAnalysis.Evidence.SourceConfigurationModel? configuration) =>
        configuration is null
            ? project.Configuration.SelectMany(c => c.Values.Select(v => (c.Path, 0, v.Key, v.Value)))
            : configuration.ForProject(project.Path).Where(v => ApplicationConfiguration.Contains(v.Technology)).Select(v => (v.File, 0, v.Key, v.Raw));
}
