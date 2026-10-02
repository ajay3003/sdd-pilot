using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Source Analysis → Configuration. Normalizes application and environment configuration across formats (appsettings and frontend
/// appsettings, launchSettings, Azure Functions settings, Docker Compose environment, Helm values, YAML/properties/.env files, Terraform
/// variables, Bicep parameters, Kubernetes ConfigMaps, pipeline variables) into one entry model: key, normalized key, category, value KIND,
/// environment, file/line, component. Secrets, passwords, tokens, connection strings, keys and certificates are classified and never kept —
/// only safe previews of non-sensitive values (booleans, numbers, endpoint scheme+host+path, entity names, public identifiers) are stored.
/// Environment-specific values are variants; only the same key with materially different values in the SAME environment and scope is a
/// potential conflict. Configured in source ≠ deployed configuration.
/// </summary>
internal sealed class ConfigurationAnalyzer : ISourceEvidenceDomainAnalyzer
{
    public const int Version = 1;
    public const int MaxEntries = 5000;
    public DomainAnalyzerInfo Info { get; } = SourceEvidenceAnalyzer.Info(SourceEvidenceDomain.Configuration, "Configuration analyzer", Version, 1,
        ["ASP.NET Core appsettings", "launchSettings", "Docker Compose", "Helm values", "Kubernetes ConfigMap", "Terraform variables", "Pipeline variables", "Properties", ".env", "YAML configuration"],
        [SourceEvidenceDomain.Infrastructure, SourceEvidenceDomain.CiCd],
        ["Configuration files", "Entries (redacted)", "Categories", "Environments", "Conflicts", "Endpoint/entity references"]);

    public void Failed(SourceEvidenceContext context, string reason) => context.Configuration = context.Envelope(new ConfigurationEvidence
    { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason, Limitations = [SourceDomainText.SourceBoundary] }, SourceEvidenceDomain.Configuration, Version);

    private sealed record Raw(string File, int Line, string Key, string Value, string Technology, SourceEnvironmentLabel Environment, string EnvironmentBasis, string? ProjectPath, bool ForceSensitive, string Scope);

    public void Analyze(SourceEvidenceContext context, CancellationToken ct)
    {
        context.Capabilities.AddRange([
            new(SourceEvidenceDomain.Configuration, "JSON (appsettings, launchSettings, Functions settings)", DomainSupport.Supported, "Parsed with System.Text.Json; keys flattened as Section:Key."),
            new(SourceEvidenceDomain.Configuration, "YAML (Compose, Helm values, YAML config)", DomainSupport.Partial, "YAML subset reader; anchors/aliases are not resolved."),
            new(SourceEvidenceDomain.Configuration, ".properties / .env", DomainSupport.Supported, "key=value lines."),
            new(SourceEvidenceDomain.Configuration, "Terraform variables / Bicep parameters / ConfigMaps", DomainSupport.Partial, "Values handed over by the Infrastructure analyzer (literals only)."),
            new(SourceEvidenceDomain.Configuration, "Pipeline variables", DomainSupport.Partial, "Variables declared in pipeline YAML; variable groups and library values are not readable from source."),
            new(SourceEvidenceDomain.Configuration, "XML (.config)", DomainSupport.Unsupported, "web.config/app.config are not read."),
        ]);
        var raws = new List<Raw>();
        var files = new List<ConfigurationFile>();
        var diagnostics = new List<SourceDomainDiagnostic>();
        var heuristic = false;
        var covered = new HashSet<string>(StringComparer.Ordinal);

        // 1. appsettings / .graphqlrc.json already parsed for Architecture — reused, not parsed again.
        foreach (var project in context.Input.Projects.Where(p => !p.IsAspireHost))
            foreach (var config in project.Configuration)
            {
                ct.ThrowIfCancellationRequested();
                covered.Add(config.Path);
                var file = context.Files.FirstOrDefault(f => f.Path == config.Path);
                var technology = file?.Technology ?? "ASP.NET Core appsettings";
                var env = config.Environment.Length == 0 ? SourceEnvironmentLabel.Default : SourceFileClassifier.Environment(config.Environment);
                foreach (var (key, value) in config.Values)
                    raws.Add(new(config.Path, file is null ? 0 : LineOf(file, key), key, value, technology, env, env.Kind == SourceEnvironmentKind.Default ? "none" : "file-name suffix", project.Path, false, project.Path));
                files.Add(File(config.Path, "json", technology, env, env.Kind == SourceEnvironmentKind.Default ? "none" : "file-name suffix", context.ComponentOf(config.Path)?.Name, config.Values.Count, true, null));
            }

        foreach (var file in context.Role(SourceFileRole.Configuration).Where(f => !covered.Contains(f.Path)))
        {
            ct.ThrowIfCancellationRequested();
            var project = context.ProjectOf(file.Path)?.Path;
            var component = context.ComponentOf(file.Path)?.Name;
            var scope = project ?? file.Directory;
            var nameEnv = SourceFileClassifier.EnvironmentFromName(file.Name);
            var env = nameEnv ?? SourceEnvironmentLabel.Default;
            var basis = nameEnv is null ? "none" : "file-name suffix";
            var before = raws.Count;
            var parsed = true;
            string format;
            switch (file.Technology)
            {
                case "launchSettings":
                    format = "json";
                    parsed = LaunchSettings(file, project, raws);
                    break;
                case "Docker Compose":
                    format = "yaml"; heuristic = true;
                    Compose(context, file, raws);
                    break;
                case "Properties file" or "Environment file":
                    format = file.Technology == "Properties file" ? "properties" : "env";
                    foreach (var (key, value, line) in KeyValueLines(file.Content, file.Technology == "Environment file"))
                        raws.Add(new(file.Path, line, key, value, file.Technology, env, basis, project, false, scope));
                    break;
                default:
                    format = file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
                    if (format == "json")
                    {
                        if (ArchitectureInput.FlattenJson(file.Content) is { } values)
                            foreach (var (key, value) in values) raws.Add(new(file.Path, LineOf(file, key), key, value, file.Technology, env, basis, project, false, scope));
                        else parsed = false;
                    }
                    else
                    {
                        heuristic = true;
                        foreach (var doc in MiniYaml.Documents(file.Content, out _))
                            foreach (var (key, value, line, block) in MiniYaml.Flatten(doc))
                                if (!block && key.Length > 0) raws.Add(new(file.Path, line, key, value ?? "", file.Technology, env, basis, project, false, scope));
                    }
                    break;
            }
            if (!parsed) diagnostics.Add(new("Parse error", "Configuration file could not be parsed; its entries are not listed.", SourceEvidenceRedaction.Safe(file.Path)));
            files.Add(File(file.Path, format, file.Technology, env, basis, component, raws.Count - before, parsed, null));
        }

        // 2. Values other domains already read (tfvars, Bicep parameters, ConfigMaps, pipeline variables).
        foreach (var group in context.PendingConfiguration.GroupBy(p => p.File, StringComparer.Ordinal))
        {
            heuristic |= group.Any(p => p.Technology is "Kubernetes ConfigMap" || p.Technology.EndsWith("variables", StringComparison.Ordinal) && !p.Technology.StartsWith("Terraform", StringComparison.Ordinal));
            var first = group.First();
            foreach (var p in group)
                raws.Add(new(p.File, p.Line, p.Key, p.Raw, p.Technology, p.Environment, p.Environment.Kind == SourceEnvironmentKind.Default ? "none" : "file or folder name", context.ProjectOf(p.File)?.Path, p.Sensitive, p.File));
            files.Add(File(first.File, first.Technology.StartsWith("Terraform") ? "hcl" : first.Technology.StartsWith("Bicep") ? "bicep" : "yaml", first.Technology, first.Environment,
                first.Environment.Kind == SourceEnvironmentKind.Default ? "none" : "file or folder name", context.ComponentOf(first.File)?.Name, group.Count(), true, null));
        }

        if (raws.Count == 0 && files.Count == 0)
        {
            context.Configuration = context.Envelope(new ConfigurationEvidence
            {
                Status = SourceDomainStatus.NotDetected, StatusReason = "No supported configuration files detected in the selected source.",
                Limitations = [SourceDomainText.SourceBoundary],
            }, SourceEvidenceDomain.Configuration, Version);
            return;
        }

        var truncated = raws.Count > MaxEntries;
        var entries = new List<ConfigurationEntry>();
        foreach (var raw in raws.Take(MaxEntries))
        {
            var (kind, sensitivity, preview) = SourceEvidenceRedaction.Classify(raw.Key, raw.Value);
            if (raw.ForceSensitive && sensitivity == ConfigurationSensitivity.None) { sensitivity = ConfigurationSensitivity.Sensitive; preview = null; if (kind is not (ConfigurationValueKind.Empty or ConfigurationValueKind.Placeholder)) kind = ConfigurationValueKind.Secret; }
            var component = raw.ProjectPath is null ? null : context.Architecture?.Components.FirstOrDefault(c => c.SourceProject == raw.ProjectPath)?.Name;
            var entry = new ConfigurationEntry
            {
                Id = Hash($"{raw.File}|{raw.Key}"), Key = SourceEvidenceRedaction.Safe(raw.Key), NormalizedKey = NormalizeKey(raw.Key), Category = Category(raw.Key, raw.Technology),
                ValueKind = kind, ValuePreviewSafe = sensitivity == ConfigurationSensitivity.None || sensitivity == ConfigurationSensitivity.SecretReference ? preview : null,
                Sensitivity = sensitivity, Environment = raw.Environment, File = SourceEvidenceRedaction.Safe(raw.File), Line = raw.Line, Technology = raw.Technology, Component = component,
                EvidenceState = raw.Technology.Contains("variables", StringComparison.Ordinal) || raw.Technology is "Kubernetes ConfigMap" ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Confirmed,
                References = References(raw.Key, raw.Value, kind, preview),
            };
            entries.Add(entry);
            context.ConfigurationModel.Values.Add(new RawConfigurationValue(raw.File, raw.Key, raw.Value, raw.Technology, raw.Environment, raw.ProjectPath, entry));
        }

        // Conflicts: only among sources ONE runtime layers together — a project's appsettings, launch profile, Compose environment, .env and
        // Functions settings for the same environment. Sibling files of one technology in different folders (per-client configs, per-hub
        // configs) are separate configurations, not conflicts; different environments are variants. Values are compared in memory only.
        var conflicts = raws.Take(MaxEntries).Where(r => Layered.Contains(r.Technology) && r.ProjectPath is not null)
            .GroupBy(r => (r.ProjectPath, r.Environment.Kind, Env: r.Environment.Raw.ToLowerInvariant(), Key: NormalizeKey(r.Key)))
            .Where(g => g.Select(r => r.File).Distinct().Count() > 1 && g.Select(r => Material(r.Value)).Distinct().Count() > 1
                && !(g.Select(r => r.Technology).Distinct().Count() == 1 && g.Select(r => System.IO.Path.GetDirectoryName(r.File)).Distinct().Count() > 1))
            .Select(g => new ConfigurationConflict(g.Key.Key, g.First().Environment, g.Select(r => SourceEvidenceRedaction.Safe(r.File)).Distinct().Order(StringComparer.Ordinal).ToList(),
                "Same key and environment with different values in these files (values not shown). Which one applies depends on load order — review."))
            .ToList();

        context.Configuration = context.Envelope(new ConfigurationEvidence
        {
            Status = heuristic || truncated || diagnostics.Count > 0 ? SourceDomainStatus.Partial : SourceDomainStatus.Complete,
            StatusReason = truncated ? $"More than {MaxEntries} entries; the first {MaxEntries} are listed." : heuristic ? "Some formats are read by a subset reader (YAML) or handed over as literals only." : null,
            Technologies = [.. files.Select(f => f.Technology).Distinct().Order(StringComparer.Ordinal)],
            Files = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], Entries = entries,
            Environments = [.. entries.Select(e => e.Environment).DistinctBy(e => (e.Kind, e.Raw.ToLowerInvariant())).OrderBy(e => e.Kind).ThenBy(e => e.Raw, StringComparer.Ordinal)],
            Conflicts = conflicts, Diagnostics = diagnostics, Truncated = truncated,
            Limitations = [SourceDomainText.SourceBoundary, "Values configured in source are not the deployed configuration: deployment settings, Key Vault, variable groups and environment overrides may differ.",
                "A file-name environment suffix is a naming convention, not proof of a deployed environment.", "Sensitive values are classified and never stored; only their key, kind and location are kept."],
        }, SourceEvidenceDomain.Configuration, Version);
    }

    private static ConfigurationFile File(string path, string format, string technology, SourceEnvironmentLabel env, string basis, string? component, int entries, bool parsed, string? note) => new()
    {
        Path = SourceEvidenceRedaction.Safe(path), Format = format, Technology = technology, Environment = env, EnvironmentBasis = basis, Component = component, Entries = entries, Parsed = parsed, Note = note,
    };

    /// <summary>Sources a .NET/containerized application layers into one configuration at startup.</summary>
    private static readonly HashSet<string> Layered = new(StringComparer.Ordinal)
        { "ASP.NET Core appsettings", "Frontend appsettings", "launchSettings", "Docker Compose", "Environment file", "Azure Functions settings" };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    private static string Material(string value) => value.Trim().ToLowerInvariant();

    public static string NormalizeKey(string key) =>
        Regex.Replace(key.Replace("__", ":"), @"\s+", "").Replace('.', ':').ToLowerInvariant().Trim(':');

    private static int LineOf(EvidenceFile file, string key)
    {
        var leaf = Regex.Replace(key, @":\d+$", "").Split(':').Last();
        var index = file.Content.IndexOf($"\"{leaf}\"", StringComparison.Ordinal);
        return index < 0 ? 0 : file.Line(index);
    }

    private static bool LaunchSettings(EvidenceFile file, string? project, List<Raw> raws)
    {
        try
        {
            using var doc = JsonDocument.Parse(file.Content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object) return true;
            foreach (var profile in profiles.EnumerateObject())
            {
                var variables = profile.Value.TryGetProperty("environmentVariables", out var v) && v.ValueKind == JsonValueKind.Object ? v.EnumerateObject().ToList() : [];
                var envName = variables.FirstOrDefault(x => x.Name is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT" or "AZURE_FUNCTIONS_ENVIRONMENT").Value;
                var env = envName.ValueKind == JsonValueKind.String ? SourceFileClassifier.Environment(envName.GetString() ?? "") : SourceEnvironmentLabel.Default;
                var basis = envName.ValueKind == JsonValueKind.String ? "launch profile variable" : "none";
                var scope = $"{file.Path}#{profile.Name}";
                foreach (var variable in variables)
                    raws.Add(new(file.Path, LineOf(file, variable.Name), variable.Name.Replace("__", ":"), variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString() ?? "" : variable.Value.GetRawText(),
                        "launchSettings", env, basis, project, false, scope));
                if (profile.Value.TryGetProperty("applicationUrl", out var url) && url.ValueKind == JsonValueKind.String)
                    foreach (var u in (url.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                        raws.Add(new(file.Path, LineOf(file, "applicationUrl"), $"{profile.Name}:applicationUrl", u, "launchSettings", env, basis, project, false, scope));
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Compose service environments as Architecture already parsed them (one Compose parser in Source Analysis).</summary>
    private static void Compose(SourceEvidenceContext context, EvidenceFile file, List<Raw> raws)
    {
        var env = SourceFileClassifier.EnvironmentFromName(file.Name) ?? SourceEnvironmentLabel.Default;
        foreach (var service in context.Input.Compose.Where(s => s.File == file.Path))
        {
            var buildDir = service.BuildContext is null ? null : ArchitectureInput.Normalize($"{file.Directory}/{service.BuildContext}").TrimEnd('/');
            var project = buildDir is null ? null : context.Input.Projects.FirstOrDefault(p => p.Directory.TrimEnd('/') == buildDir || p.Directory.TrimEnd('/').StartsWith(buildDir + "/", StringComparison.Ordinal))?.Path;
            foreach (var (key, value) in service.Environment)
                raws.Add(new(file.Path, service.Line, key.Replace("__", ":"), value, "Docker Compose", env, env.Kind == SourceEnvironmentKind.Default ? "none" : "file-name suffix", project, false, $"{file.Path}#{service.Name}"));
        }
    }

    private static IEnumerable<(string Key, string Value, int Line)> KeyValueLines(string content, bool env)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
            if (env && line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var sep = line.IndexOfAny(env ? ['='] : ['=', ':']);
            if (sep <= 0) continue;
            var value = line[(sep + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            yield return (line[..sep].Trim().Replace("__", ":"), value, i + 1);
        }
    }

    // ── Categories ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly (ConfigurationCategory Category, Regex Pattern)[] Categories =
    [
        (ConfigurationCategory.Authentication, R(@"(^|:)(azuread|azure_ad|entra|entraid|authentication|auth|msal|oidc|openid(connect)?|jwt(bearer)?|identityserver|oauth2?|b2c)(:|$)|(^|:)(authority|tenantid|tenant_id|clientid|client_id|audience|validissuers?|issuer|callbackpath|signedoutcallbackpath|redirecturis?|redirecturls?|scopes?|instance)$")),
        (ConfigurationCategory.Authorization, R(@"(^|:)(authorization|policies|permissions|roles?|rbac|claims)(:|$)")),
        (ConfigurationCategory.Messaging, R(@"(servicebus|service_bus|eventhubs?|event_hub|kafka|rabbitmq|amqp|wolverine|masstransit|messaging|broker|nats|pubsub|sqs|sns|(^|:)(topics?|queues?|subscriptions?|consumergroups?|consumer_group|hubname|eventhubname)(:|$))")),
        (ConfigurationCategory.Database, R(@"((^|:)connectionstrings?(:|$)|database|(^|:)db(:|_|$)|postgres|npgsql|(^|:)sql|cosmos|mongo|dbcontext|(^|:)ef(:|$)|migrations)")),
        (ConfigurationCategory.Storage, R(@"(storage|blob|bucket|(^|:)s3(:|$)|fileshare|datalake|checkpoint)")),
        (ConfigurationCategory.Observability, R(@"(logging|serilog|nlog|log4net|applicationinsights|application_insights|appinsights|opentelemetry|otel|otlp|telemetry|tracing|metrics|sampling|loglevel|seq(:|$)|datadog|elastic(apm)?|healthchecks?)")),
        (ConfigurationCategory.Security, R(@"(securityheaders|hsts|(^|:)csp(:|$)|contentsecuritypolicy|dataprotection|certificates?|encryption|keyvault|key_vault|(^|:)secrets?(:|$)|(^|:)tls|(^|:)ssl|antiforgery)")),
        (ConfigurationCategory.FeatureFlags, R(@"(featuremanagement|featureflags?|(^|:)features?(:|$)|toggles?|(^|:)flags?(:|$))")),
        (ConfigurationCategory.Performance, R(@"(timeout|retry|retries|polly|ratelimit|rate_limit|throttl|maxconcurren|concurrency|batchsize|prefetch|poolsize|maxpool|circuitbreaker|(^|:)cache(:|$)|caching)")),
        (ConfigurationCategory.Networking, R(@"(cors|allowedhosts|kestrel|(^|:)urls?(:|$)|applicationurl|reverseproxy|yarp|forwardedheaders|(^|:)ports?(:|$)|(^|:)hosts?(:|$)|https?_?port|proxy)")),
        (ConfigurationCategory.Api, R(@"(graphql|(^|:)api(:|$)|apibaseurl|baseurl|baseaddress|(^|:)endpoint(s)?(:|$)|swagger|openapi|httpclients?|(^|:)clients(:|$)|downstream|restapi|webapi|grpc)")),
        (ConfigurationCategory.ExternalSystems, R(@"((^|:)(integrations?|external|thirdparty|partners?)(:|$))")),
        (ConfigurationCategory.RuntimeEnvironment, R(@"(aspnetcore_|dotnet_|azure_functions_|(^|:)environment(name)?(:|$)|(^|:)runtime|applicationname|(^|:)hosting|(^|:)region|(^|:)location|buildconfiguration)")),
    ];

    public static ConfigurationCategory Category(string key, string technology)
    {
        var k = NormalizeKey(key);
        foreach (var (category, pattern) in Categories) if (pattern.IsMatch(k)) return category;
        return ConfigurationCategory.Unknown;
    }

    // ── References (for cross-domain linking): hosts and entity names, never values that are secrets ─────────────────────────

    private static readonly Regex Host = new(@"^(?=.{4,253}$)([a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConnectionHost = new(@"(?i)(?:server|data source|host|endpoint|accountname|address)\s*=\s*(?:tcp:|sb://|https?://)?([A-Za-z0-9.\-]+)", RegexOptions.Compiled);

    private static List<string> References(string key, string raw, ConfigurationValueKind kind, string? preview)
    {
        var refs = new List<string>();
        switch (kind)
        {
            case ConfigurationValueKind.Url when preview is not null && Uri.TryCreate(preview, UriKind.Absolute, out var uri) && !uri.IsLoopback: refs.Add($"host:{uri.Host.ToLowerInvariant()}"); break;
            case ConfigurationValueKind.EntityName when preview is not null:
                refs.AddRange(preview.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).Select(v => Host.IsMatch(v) && v.Contains('.') ? $"host:{v.ToLowerInvariant()}" : $"entity:{v}"));
                break;
            case ConfigurationValueKind.Text when preview is not null && Host.IsMatch(preview) && preview.Count(c => c == '.') >= 2: refs.Add($"host:{preview.ToLowerInvariant()}"); break;
            case ConfigurationValueKind.ConnectionString:
                // The host of a connection string is an address, not a credential: kept as a normalized reference for linking only.
                foreach (Match m in ConnectionHost.Matches(raw))
                {
                    var host = m.Groups[1].Value.Trim('.').ToLowerInvariant();
                    if (host.Length > 0 && (Host.IsMatch(host) || Regex.IsMatch(host, @"^[a-z0-9][a-z0-9\-]{2,62}$")) && host is not ("localhost" or "127.0.0.1")) refs.Add(host.Contains('.') ? $"host:{host}" : $"account:{host}");
                }
                break;
        }
        return refs.Distinct().ToList();
    }
}
