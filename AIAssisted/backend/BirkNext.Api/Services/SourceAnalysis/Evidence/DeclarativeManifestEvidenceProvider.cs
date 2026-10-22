using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using static BirkNext.Api.Services.SourceAnalysis.Evidence.InfrastructureEvidenceBuilder;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Infrastructure evidence provider: declarative manifests read by pattern (Partial) — Bicep, ARM templates, Kubernetes manifests and Helm
/// charts. Expressions, loops and templates are not evaluated or rendered.
/// </summary>
internal sealed partial class DeclarativeManifestEvidenceProvider : IInfrastructureSourceEvidenceProvider
{
    // ── Bicep (pattern-based, Partial) ──────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex BicepResource = new(@"(?m)^\s*resource\s+([A-Za-z_]\w*)\s+'([^'@]+)@([^']+)'\s*(existing\s*)?=", RegexOptions.Compiled);
    private static readonly Regex BicepParam = new(@"(?m)^(\s*@secure\(\)\s*\n)?\s*param\s+([A-Za-z_]\w*)\s+([A-Za-z_]\w*)(\s*=\s*(.+))?$", RegexOptions.Compiled);
    private static readonly Regex BicepModule = new(@"(?m)^\s*module\s+([A-Za-z_]\w*)\s+'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex BicepOutput = new(@"(?m)^\s*output\s+([A-Za-z_]\w*)\s+\w+", RegexOptions.Compiled);
    private static readonly Regex BicepSetting = new(@"(?m)^\s*([A-Za-z_]\w*)\s*:\s*('([^'$]*)'|true|false|-?\d+)\s*$", RegexOptions.Compiled);

    private static string Snake(string camel) => Regex.Replace(camel, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    private static void Bicep(InfrastructureEvidenceBuilder b, List<EvidenceFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        b.Formats.Add(InfrastructureFormat.Bicep);
        b.Technologies.Add("Bicep");
        b.Heuristic = true;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var safeFile = SourceEvidenceRedaction.Safe(file.Path);
            if (file.Path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase))
            {
                var env = SourceFileClassifier.EnvironmentFromName(file.Name) ?? FolderEnvironment(file.Path) ?? SourceEnvironmentLabel.Default;
                b.Environment(env, file.Path, "parameter file name");
                foreach (Match m in Regex.Matches(file.Content, @"(?m)^\s*param\s+([A-Za-z_]\w*)\s*=\s*'([^']*)'"))
                    b.Pending.Add(new(file.Path, file.Line(m.Index), m.Groups[1].Value, m.Groups[2].Value, "Bicep parameters", env, SourceEvidenceRedaction.SensitiveKey(m.Groups[1].Value)));
                continue;
            }
            var symbols = BicepResource.Matches(file.Content).ToDictionary(m => m.Groups[1].Value, m => Id(file.Path, m.Groups[1].Value), StringComparer.Ordinal);
            var matches = BicepResource.Matches(file.Content).ToList();
            for (var i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var body = file.Content[m.Index..(i + 1 < matches.Count ? matches[i + 1].Index : file.Content.Length)];
                var type = m.Groups[2].Value;
                var (category, detail) = InfrastructureCatalog.AzureResourceType(type);
                var name = Regex.Match(body, @"(?m)^\s*name\s*:\s*'([^'$]*)'\s*$") is { Success: true } n ? SourceEvidenceRedaction.SafeLiteral("name", n.Groups[1].Value) : null;
                var id = symbols[m.Groups[1].Value];
                b.Resources.Add(new InfrastructureResource
                {
                    Id = id, Format = InfrastructureFormat.Bicep, Kind = m.Groups[4].Success ? "data" : "resource", Provider = "azure", ResourceType = type, LogicalName = m.Groups[1].Value,
                    DeclaredName = name, DeclaredNameResolved = name is not null, Category = category, CategoryDetail = detail, File = safeFile, Line = file.Line(m.Index),
                    Environment = FolderEnvironment(file.Path), EvidenceState = ArchitectureEvidenceState.Confirmed,
                    Settings = BicepSetting.Matches(body).Select(s => (Key: Snake(s.Groups[1].Value), Raw: s.Groups[3].Success ? s.Groups[3].Value : s.Groups[2].Value))
                        .Where(s => InfrastructureCatalog.SettingArea(s.Key) is not null && s.Key != "name")
                        .Select(s => new InfrastructureSetting(s.Key, SourceEvidenceRedaction.SensitiveKey(s.Key) ? "[sensitive — value not shown]" : SourceEvidenceRedaction.SafeLiteral(s.Key, s.Raw) ?? "[sensitive — value not shown]", InfrastructureCatalog.SettingArea(s.Key)!, true))
                        .DistinctBy(s => s.Key).ToList(),
                    RuntimeState = m.Groups[4].Success ? "Existing resource reference; nothing is declared" : SourceDomainText.DeclaredNotVerified,
                });
                foreach (Match r in Regex.Matches(body, @"\b([A-Za-z_]\w*)\.(id|name|properties)\b"))
                    if (symbols.TryGetValue(r.Groups[1].Value, out var to) && to != id) b.Dependencies.Add(new(id, to, "reference", ArchitectureEvidenceState.Confirmed, safeFile, file.Line(m.Index + r.Index)));
                if (Regex.Match(body, @"(?m)^\s*parent\s*:\s*([A-Za-z_]\w*)") is { Success: true } parent && symbols.TryGetValue(parent.Groups[1].Value, out var p))
                    b.Dependencies.Add(new(id, p, "parent", ArchitectureEvidenceState.Confirmed, safeFile, file.Line(m.Index + parent.Index)));
            }
            foreach (Match m in BicepParam.Matches(file.Content))
            {
                var secure = m.Groups[1].Success;
                var raw = m.Groups[5].Success ? m.Groups[5].Value.Trim() : null;
                var literal = raw is not null && raw.StartsWith('\'') && raw.EndsWith('\'') ? raw[1..^1] : raw;
                var (state, safe) = raw is null ? ("none", (string?)null) : secure || SourceEvidenceRedaction.SensitiveKey(m.Groups[2].Value) || SourceEvidenceRedaction.SecretShaped(literal)
                    ? ("redacted", null) : SourceEvidenceRedaction.SafeLiteral(m.Groups[2].Value, literal) is { } s ? ("literal", s) : ("expression", null);
                b.Variables.Add(new InfrastructureVariable { Name = m.Groups[2].Value, Type = m.Groups[3].Value, Sensitive = secure, DefaultState = state, SafeDefault = safe, File = safeFile, Line = file.Line(m.Index) });
            }
            foreach (Match m in BicepModule.Matches(file.Content))
            {
                var source = m.Groups[2].Value;
                var local = !source.Contains(':');
                var resolved = local ? Normalize($"{Dir(file.Path)}/{source}") : null;
                var analyzed = resolved is not null && files.Any(f => f.Path == resolved);
                b.Modules.Add(new InfrastructureModule { Id = Id(file.Path, "module." + m.Groups[1].Value), Name = m.Groups[1].Value, Source = SafeSource(source), Local = local, ResolvedPath = resolved, Analyzed = analyzed,
                    File = safeFile, Line = file.Line(m.Index), Note = local ? analyzed ? null : "Local module file not found in the selected source." : ExternalModule });
                if (!local) b.Limitations.Add(ExternalModule);
            }
            b.Outputs.AddRange(BicepOutput.Matches(file.Content).Select(m => new InfrastructureOutput(m.Groups[1].Value, false, [], safeFile, file.Line(m.Index))));
        }
    }

    // ── ARM templates (Partial) ─────────────────────────────────────────────────────────────────────────────────────────

    private static void Arm(InfrastructureEvidenceBuilder b, List<EvidenceFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        b.Formats.Add(InfrastructureFormat.Arm);
        b.Technologies.Add("ARM template");
        b.Heuristic = true;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var safeFile = SourceEvidenceRedaction.Safe(file.Path);
            try
            {
                using var doc = JsonDocument.Parse(file.Content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
                    foreach (var p in parameters.EnumerateObject())
                    {
                        var type = p.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        var secure = type.StartsWith("secure", StringComparison.OrdinalIgnoreCase);
                        b.Variables.Add(new InfrastructureVariable { Name = SourceEvidenceRedaction.Safe(p.Name), Type = SourceEvidenceRedaction.Safe(type), Sensitive = secure,
                            DefaultState = p.Value.TryGetProperty("defaultValue", out _) ? secure ? "redacted" : "expression" : "none", File = safeFile });
                    }
                if (doc.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
                    foreach (var r in resources.EnumerateArray())
                    {
                        var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var literal = name is not null && !name.StartsWith('[') ? SourceEvidenceRedaction.SafeLiteral("name", name) : null;
                        var (category, detail) = InfrastructureCatalog.AzureResourceType(type);
                        b.Resources.Add(new InfrastructureResource
                        {
                            Id = Id(file.Path, $"{type}/{literal ?? "(expression)"}"), Format = InfrastructureFormat.Arm, Provider = "azure", ResourceType = SourceEvidenceRedaction.Safe(type),
                            LogicalName = literal ?? "(expression name)", DeclaredName = literal, DeclaredNameResolved = literal is not null, Category = category, CategoryDetail = detail, File = safeFile,
                            EvidenceState = ArchitectureEvidenceState.Confirmed,
                        });
                    }
            }
            catch (JsonException) { b.Diagnostics.Add(new("Parse error", "ARM template JSON could not be parsed.", safeFile)); }
        }
    }

    // ── Kubernetes manifests (Partial) ──────────────────────────────────────────────────────────────────────────────────

    private static void Kubernetes(InfrastructureEvidenceBuilder b, List<EvidenceFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        b.Formats.Add(InfrastructureFormat.Kubernetes);
        b.Technologies.Add("Kubernetes");
        b.Heuristic = true;
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new List<(EvidenceFile File, YamlNode Doc, string Kind, string Name, string Namespace)>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var doc in MiniYaml.Documents(file.Content, out _).Where(d => d.Kind == YamlKind.Map))
            {
                var kind = doc.Str("kind");
                var name = doc["metadata"]?.Str("name");
                if (kind is null || name is null) continue;
                var ns = doc["metadata"]?.Str("namespace") ?? "default";
                parsed.Add((file, doc, kind, name, ns));
                declared[$"{ns}/{kind}/{name}"] = Id(file.Path, $"{kind}/{name}");
            }
        }
        foreach (var (file, doc, kind, name, ns) in parsed)
        {
            var id = declared[$"{ns}/{kind}/{name}"];
            var safeFile = SourceEvidenceRedaction.Safe(file.Path);
            var (category, detail) = InfrastructureCatalog.KubernetesKind(kind);
            var settings = new List<InfrastructureSetting>();
            var spec = doc["spec"];
            var pod = spec?["template"]?["spec"] ?? (kind == "Pod" ? spec : spec?["jobTemplate"]?["spec"]?["template"]?["spec"]);
            void Setting(string key, string? value, string area) { if (value is not null) settings.Add(new(key, SourceEvidenceRedaction.SafeLiteral(key, value) ?? "(value)", area, true)); }
            Setting("serviceAccountName", pod?.Str("serviceAccountName"), "Identity");
            Setting("securityContext.runAsNonRoot", pod?["securityContext"]?.Str("runAsNonRoot"), "Security");
            Setting("hostNetwork", pod?.Str("hostNetwork"), "Networking");
            Setting("type", kind == "Service" ? spec?.Str("type") : null, "Networking");
            if (kind == "Ingress") settings.Add(new("tls", spec?.Has("tls") == true ? "declared" : "not declared", "Security", true));
            if (kind == "Secret") settings.Add(new("data keys", $"{(doc["data"]?.Entries.Count ?? 0) + (doc["stringData"]?.Entries.Count ?? 0)} (values not read)", "Security", true));
            b.Resources.Add(new InfrastructureResource
            {
                Id = id, Format = InfrastructureFormat.Kubernetes, Provider = "kubernetes", ResourceType = SourceEvidenceRedaction.Safe(kind), LogicalName = SourceEvidenceRedaction.Safe(name),
                DeclaredName = SourceEvidenceRedaction.SafeLiteral("name", name), DeclaredNameResolved = true, Category = category, CategoryDetail = detail,
                File = safeFile, Line = doc.Line, Environment = FolderEnvironment(file.Path), Settings = settings, EvidenceState = ArchitectureEvidenceState.Confirmed,
            });
            if (FolderEnvironment(file.Path) is { } env) b.Environment(env, file.Path, "folder name");
            if (kind == "ConfigMap" && doc["data"] is { Kind: YamlKind.Map } data)
                foreach (var (key, value) in data.Entries)
                    b.Pending.Add(new(file.Path, value.Line, key, value.Value ?? "", "Kubernetes ConfigMap", FolderEnvironment(file.Path) ?? SourceEnvironmentLabel.Default, SourceEvidenceRedaction.SensitiveKey(key)));
            void Depend(string targetKind, string? targetName, string via)
            {
                if (targetName is not null && declared.TryGetValue($"{ns}/{targetKind}/{targetName}", out var to)) b.Dependencies.Add(new(id, to, via, ArchitectureEvidenceState.Confirmed, safeFile, doc.Line));
            }
            Depend("ServiceAccount", pod?.Str("serviceAccountName"), "serviceAccountName");
            foreach (var container in pod?.List("containers") ?? [])
                foreach (var from in container.List("envFrom"))
                { Depend("ConfigMap", from["configMapRef"]?.Str("name"), "envFrom"); Depend("Secret", from["secretRef"]?.Str("name"), "envFrom"); }
            foreach (var volume in pod?.List("volumes") ?? [])
            { Depend("ConfigMap", volume["configMap"]?.Str("name"), "volume"); Depend("Secret", volume["secret"]?.Str("secretName"), "volume"); }
            if (kind is "RoleBinding" or "ClusterRoleBinding")
                b.Access.Add(new AccessAssignment { ResourceId = id, Role = doc["roleRef"]?.Str("name"), ScopeReference = kind == "ClusterRoleBinding" ? "cluster" : $"namespace {ns}",
                    PrincipalReference = string.Join(", ", doc.List("subjects").Select(s => $"{s.Str("kind")}/{s.Str("name")}").Take(3)), EvidenceState = ArchitectureEvidenceState.Confirmed });
        }
    }

    private static void Helm(InfrastructureEvidenceBuilder b, List<EvidenceFile> files)
    {
        if (files.Count == 0) return;
        b.Formats.Add(InfrastructureFormat.Helm);
        b.Technologies.Add("Helm");
        b.Heuristic = true;
        foreach (var chart in files.Where(f => f.Technology == "Helm chart"))
        {
            var doc = MiniYaml.Parse(chart.Content);
            b.Modules.Add(new InfrastructureModule
            {
                Id = Id(chart.Path, "chart"), Name = SourceEvidenceRedaction.Safe(doc?.Str("name") ?? chart.Directory), Source = SourceEvidenceRedaction.Safe(chart.Directory), Local = true,
                ResolvedPath = chart.Directory, Analyzed = false, Version = doc?.Str("version") is { } v ? SourceEvidenceRedaction.Safe(v) : null, File = SourceEvidenceRedaction.Safe(chart.Path), Line = 1,
                Note = "Helm chart: templates are not rendered; values files are analyzed as configuration.",
            });
        }
        var templates = files.Count(f => f.Technology == "Helm template");
        if (templates > 0) b.Diagnostics.Add(new("Not rendered", $"{templates} Helm template file(s) were not rendered; resources they produce are not listed."));
    }
}
