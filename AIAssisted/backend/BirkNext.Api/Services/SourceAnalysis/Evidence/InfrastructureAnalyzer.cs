using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Source Analysis → Infrastructure as Code. Terraform is read structurally (HCL blocks, attributes, references, local modules, variables,
/// tfvars, outputs, providers, backend metadata); Bicep, ARM, Kubernetes manifests and Helm charts are recognized by pattern (Partial).
/// Nothing is executed: no terraform init/plan/apply, no module download, no state, no provider call. Every resource is "declared in source";
/// existence, effective permission, reachability, delivery and processing are runtime questions other reviews answer.
/// </summary>
internal sealed class InfrastructureAnalyzer : ISourceEvidenceDomainAnalyzer
{
    /// <summary>v2: per-environment declared names (tfvars). Snapshots analysed by v1 keep their v1 evidence; nothing is reinterpreted.</summary>
    public const int Version = 2;
    public DomainAnalyzerInfo Info { get; } = SourceEvidenceAnalyzer.Info(SourceEvidenceDomain.Infrastructure, "Infrastructure as Code analyzer", Version, 1,
        ["Terraform", "Terraform variables", "Bicep", "ARM template", "Kubernetes manifest", "Helm chart"], [],
        ["Resources", "Data sources", "Modules", "Static dependencies", "Variables", "Outputs", "Providers", "Backends", "Access assignments", "Environments"]);

    private const string ExternalModule = "External module not analyzed — module sources are never fetched.";


    public void Failed(SourceEvidenceContext context, string reason) => context.Infrastructure = context.Envelope(new InfrastructureEvidence
    { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason, Limitations = [SourceDomainText.SourceBoundary] }, SourceEvidenceDomain.Infrastructure, Version);

    private sealed class Builder
    {
        public List<InfrastructureResource> Resources { get; } = [];
        public List<InfrastructureModule> Modules { get; } = [];
        public List<InfrastructureDependency> Dependencies { get; } = [];
        public List<InfrastructureVariable> Variables { get; } = [];
        public List<string> Locals { get; } = [];
        public List<InfrastructureOutput> Outputs { get; } = [];
        public Dictionary<string, InfrastructureProvider> Providers { get; } = new(StringComparer.Ordinal);
        public List<InfrastructureBackend> Backends { get; } = [];
        public List<AccessAssignment> Access { get; } = [];
        public Dictionary<string, (SourceEnvironmentLabel Label, HashSet<string> Files, string Basis)> Environments { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unresolved { get; } = new(StringComparer.Ordinal);
        public List<SourceDomainDiagnostic> Diagnostics { get; } = [];
        public HashSet<string> Limitations { get; } = new(StringComparer.Ordinal) { SourceDomainText.SourceBoundary };
        public HashSet<InfrastructureFormat> Formats { get; } = [];
        public HashSet<string> Technologies { get; } = new(StringComparer.Ordinal);
        public List<PendingConfigurationValue> Pending { get; } = [];
        public bool Heuristic { get; set; }

        public void Environment(SourceEnvironmentLabel label, string file, string basis)
        {
            var key = $"{label.Kind}|{label.Raw.ToLowerInvariant()}";
            if (!Environments.TryGetValue(key, out var e)) Environments[key] = e = (label, new HashSet<string>(StringComparer.Ordinal), basis);
            e.Files.Add(SourceEvidenceRedaction.Safe(file));
        }
    }

    public void Analyze(SourceEvidenceContext context, CancellationToken ct)
    {
        var files = context.Role(SourceFileRole.InfrastructureAsCode).ToList();
        context.Capabilities.AddRange([
            new(SourceEvidenceDomain.Infrastructure, "Terraform", DomainSupport.Supported, "HCL structure, resources, data sources, references, depends_on, local modules, variables, tfvars, outputs, providers and backend metadata. Static only: no expression evaluation, count/for_each not expanded, no state, plan or provider call."),
            new(SourceEvidenceDomain.Infrastructure, "Bicep", DomainSupport.Partial, "Resource, parameter, module and output declarations by pattern; expressions and loops are not evaluated."),
            new(SourceEvidenceDomain.Infrastructure, "ARM template", DomainSupport.Partial, "Top-level resources and parameters; template expressions and nested deployments are not evaluated."),
            new(SourceEvidenceDomain.Infrastructure, "Kubernetes manifest", DomainSupport.Partial, "Kinds, names, namespaces and selected security/network settings via a YAML subset reader; Kustomize overlays are not applied."),
            new(SourceEvidenceDomain.Infrastructure, "Helm chart", DomainSupport.Partial, "Chart metadata and values files; templates are not rendered."),
            new(SourceEvidenceDomain.Infrastructure, "Pulumi / AWS CDK / CloudFormation", DomainSupport.Unsupported, "Detected only; not analyzed."),
        ]);
        if (files.Count == 0)
        {
            context.Infrastructure = context.Envelope(new InfrastructureEvidence
            {
                Status = SourceDomainStatus.NotDetected, StatusReason = "No supported Infrastructure as Code files detected in the selected source. Infrastructure may live in another repository.",
                Limitations = [SourceDomainText.SourceBoundary],
            }, SourceEvidenceDomain.Infrastructure, Version);
            return;
        }
        var b = new Builder();
        Terraform(b, files.Where(f => f.Technology is "Terraform" or "Terraform variables").ToList(), ct);
        Bicep(b, files.Where(f => f.Technology == "Bicep").ToList(), ct);
        Arm(b, files.Where(f => f.Technology == "ARM template").ToList(), ct);
        Kubernetes(b, files.Where(f => f.Technology == "Kubernetes manifest").ToList(), ct);
        Helm(b, files.Where(f => f.Technology is "Helm chart" or "Helm template").ToList());
        var unsupported = files.Where(f => f.Technology == "Pulumi").ToList();
        if (unsupported.Count > 0)
        {
            b.Technologies.Add("Pulumi (unsupported)");
            b.Diagnostics.Add(new("Unsupported format", $"{unsupported.Count} Pulumi file(s) detected; Pulumi programs are not analyzed.", SourceEvidenceRedaction.Safe(unsupported[0].Path)));
        }
        context.PendingConfiguration.AddRange(b.Pending);

        var analyzedAny = b.Formats.Count > 0;
        var status = !analyzedAny ? SourceDomainStatus.Unsupported
            : b.Heuristic || b.Unresolved.Count > 0 || b.Modules.Any(m => !m.Analyzed) || b.Diagnostics.Any(d => d.Kind is "Parse error" or "Unsupported format") ? SourceDomainStatus.Partial
            : SourceDomainStatus.Complete;
        context.Infrastructure = context.Envelope(new InfrastructureEvidence
        {
            Status = status,
            StatusReason = status switch
            {
                SourceDomainStatus.Unsupported => "Infrastructure as Code was detected, but only in unsupported formats.",
                SourceDomainStatus.Partial => "Some declarations could only be read partially (heuristic formats, external modules, unresolved references or parse errors).",
                _ => null,
            },
            Formats = [.. b.Formats.Order()], Technologies = [.. b.Technologies.Order(StringComparer.Ordinal)],
            Providers = [.. b.Providers.Values.OrderBy(p => p.Name, StringComparer.Ordinal)], Modules = b.Modules, Resources = b.Resources,
            Variables = b.Variables, Locals = [.. b.Locals.Distinct().Order(StringComparer.Ordinal)], Outputs = b.Outputs,
            Dependencies = [.. b.Dependencies.DistinctBy(d => (d.FromId, d.ToId, d.Kind))], Backends = b.Backends, AccessAssignments = b.Access,
            Environments = [.. b.Environments.Values.OrderBy(e => e.Label.Kind).ThenBy(e => e.Label.Raw, StringComparer.Ordinal).Select(e => new InfrastructureEnvironment(e.Label, [.. e.Files.Order(StringComparer.Ordinal)], e.Basis))],
            UnresolvedReferences = [.. b.Unresolved.Order(StringComparer.Ordinal)], Diagnostics = b.Diagnostics, Limitations = [.. b.Limitations],
        }, SourceEvidenceDomain.Infrastructure, Version);
    }

    // ── Terraform ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record TfModuleScope(string Directory, List<(EvidenceFile File, HclBlock Block)> Blocks)
    {
        public Dictionary<string, (string Default, bool Sensitive)> Defaults { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> LocalExpressions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ResourceIds { get; } = new(StringComparer.Ordinal);
    }

    private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static string Id(string dir, string address) => $"{(dir.Length == 0 ? "." : dir)}/{address}";
    private static string Normalize(string path) => SourceArchitecture.ArchitectureInput.Normalize(path);

    private static SourceEnvironmentLabel? FolderEnvironment(string path)
    {
        foreach (var segment in Dir(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var env = SourceFileClassifier.Environment(segment);
            if (env.Kind is not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom)) return env;
        }
        return null;
    }

    private static void Terraform(Builder b, List<EvidenceFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        b.Formats.Add(InfrastructureFormat.Terraform);
        b.Technologies.Add("Terraform");
        var scopes = new Dictionary<string, TfModuleScope>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.Technology == "Terraform"))
        {
            ct.ThrowIfCancellationRequested();
            var root = HclReader.Parse(file.Content, out var errors);
            foreach (var error in errors.Take(3)) b.Diagnostics.Add(new("Parse error", $"HCL could not be fully read: {SourceEvidenceRedaction.Safe(error)}", SourceEvidenceRedaction.Safe(file.Path)));
            var dir = Dir(file.Path);
            if (!scopes.TryGetValue(dir, out var scope)) scopes[dir] = scope = new TfModuleScope(dir, []);
            scope.Blocks.AddRange(root.Blocks.Select(block => (file, block)));
        }

        // Local module directories (by module blocks with a relative source); their resources carry the module path.
        var moduleDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes.Values)
            foreach (var (_, block) in scope.Blocks.Where(x => x.Block.Type == "module"))
                if (block.Attribute("source") is { } src && HclReader.Literal(src.Expression) is { } source && (source.StartsWith("./") || source.StartsWith("../")))
                    moduleDirs.Add(Normalize(scope.Directory.Length == 0 ? source : $"{scope.Directory}/{source}"));

        // Pass 1: symbol tables per module scope.
        foreach (var scope in scopes.Values)
            foreach (var (_, block) in scope.Blocks)
            {
                if (block.Type == "variable" && block.Labels.Count == 1)
                {
                    var sensitive = block.Attribute("sensitive") is { } s && HclReader.Literal(s.Expression) == "true";
                    if (block.Attribute("default") is { } d && HclReader.Literal(d.Expression) is { } literal) scope.Defaults[block.Labels[0]] = (literal, sensitive);
                }
                else if (block.Type == "locals")
                    foreach (var a in block.Attributes) scope.LocalExpressions[a.Name] = a.Expression;
                else if (block.Type is "resource" && block.Labels.Count == 2) scope.ResourceIds[$"{block.Labels[0]}.{block.Labels[1]}"] = Id(scope.Directory, $"{block.Labels[0]}.{block.Labels[1]}");
                else if (block.Type is "data" && block.Labels.Count == 2) scope.ResourceIds[$"data.{block.Labels[0]}.{block.Labels[1]}"] = Id(scope.Directory, $"data.{block.Labels[0]}.{block.Labels[1]}");
            }

        // Per-environment resolution: while set, variable values from one tfvars file take precedence over defaults (literals only).
        Dictionary<string, string>? overrides = null;
        var names = new List<(TfModuleScope Scope, int Index, string Expression)>();
        string? Resolve(TfModuleScope scope, string expression, int depth = 0)
        {
            if (depth > 3) return null;
            var e = expression.Trim();
            if (HclReader.Literal(e) is { } literal) return literal;
            var single = Regex.Match(e, @"^(var|local)\.([A-Za-z_][A-Za-z0-9_\-]*)$");
            if (single.Success) return Lookup(single.Groups[1].Value, single.Groups[2].Value);
            if (e.Length >= 2 && e[0] == '"' && e[^1] == '"')
            {
                var failed = false;
                var resolved = Regex.Replace(e[1..^1], @"\$\{\s*(var|local)\.([A-Za-z_][A-Za-z0-9_\-]*)\s*\}", m => Lookup(m.Groups[1].Value, m.Groups[2].Value) ?? (failed = true).ToString());
                return failed || resolved.Contains("${") ? null : resolved;
            }
            return null;
            string? Lookup(string kind, string name) => kind == "var"
                ? overrides is not null && overrides.TryGetValue(name, out var o) ? o
                : scope.Defaults.TryGetValue(name, out var d) && !d.Sensitive ? d.Default : null
                : scope.LocalExpressions.TryGetValue(name, out var l) ? Resolve(scope, l, depth + 1) : null;
        }

        string? Target(TfModuleScope scope, string reference)
        {
            var parts = reference.Split('.');
            if (parts[0] == "module") return Id(scope.Directory, $"module.{parts[1]}");
            if (parts[0] == "data" && parts.Length >= 3) return scope.ResourceIds.GetValueOrDefault($"data.{parts[1]}.{parts[2]}");
            if (parts[0] is "var" or "local" or "each" or "count" or "path" or "self" or "terraform") return null;
            return parts.Length >= 2 ? scope.ResourceIds.GetValueOrDefault($"{parts[0]}.{parts[1]}") : null;
        }

        // Pass 2: declarations.
        foreach (var scope in scopes.Values.OrderBy(s => s.Directory, StringComparer.Ordinal))
        {
            var modulePath = moduleDirs.Contains(scope.Directory) ? scope.Directory : null;
            foreach (var (file, block) in scope.Blocks)
            {
                ct.ThrowIfCancellationRequested();
                var safeFile = SourceEvidenceRedaction.Safe(file.Path);
                switch (block.Type)
                {
                    case "resource" or "data" when block.Labels.Count == 2:
                    {
                        var type = block.Labels[0];
                        var id = Id(scope.Directory, block.Type == "data" ? $"data.{type}.{block.Labels[1]}" : $"{type}.{block.Labels[1]}");
                        var provider = InfrastructureCatalog.Provider(type);
                        var (category, detail) = InfrastructureCatalog.Terraform(type);
                        if (!InfrastructureCatalog.KnownProvider(provider))
                            b.Diagnostics.Add(new("Unsupported provider", $"Provider '{SourceEvidenceRedaction.Safe(provider)}' has no category adapter; its resources are recorded generically.", safeFile, block.Line));
                        var nameAttribute = block.Attribute("name") ?? block.Attribute("bucket") ?? block.Nested("metadata").FirstOrDefault()?.Attribute("name");
                        var declared = nameAttribute is null ? null : Resolve(scope, nameAttribute.Expression);
                        // Only names built from variables/locals vary per environment; a literal name is environment-independent.
                        if (nameAttribute is not null && modulePath is null && block.Type == "resource" && HclReader.References(nameAttribute.Expression).Any(r => r.StartsWith("var.", StringComparison.Ordinal) || r.StartsWith("local.", StringComparison.Ordinal)))
                            names.Add((scope, b.Resources.Count, nameAttribute.Expression));
                        var safeName = declared is null ? null : SourceEvidenceRedaction.SafeLiteral("name", declared);
                        b.Resources.Add(new InfrastructureResource
                        {
                            Id = id, Format = InfrastructureFormat.Terraform, Kind = block.Type, Provider = provider, ResourceType = type, LogicalName = block.Labels[1],
                            DeclaredName = safeName, DeclaredNameResolved = safeName is not null, Category = category, CategoryDetail = detail, ModulePath = modulePath,
                            File = safeFile, Line = block.Line, Environment = FolderEnvironment(file.Path), Settings = Settings(scope, block, Resolve, Target),
                            TagKeys = block.Attribute("tags") is { } tags ? HclReader.ObjectKeys(tags.Expression).Select(SourceEvidenceRedaction.Safe).ToList() : [],
                            EvidenceState = ArchitectureEvidenceState.Confirmed,
                            RuntimeState = block.Type == "data" ? "Data source: reads existing infrastructure; nothing is declared" : SourceDomainText.DeclaredNotVerified,
                        });
                        if (FolderEnvironment(file.Path) is { } env) b.Environment(env, file.Path, "folder name");
                        foreach (var (_, attribute) in block.AllAttributes())
                        {
                            var explicitDependency = attribute.Name == "depends_on";
                            foreach (var reference in HclReader.References(attribute.Expression))
                            {
                                if (Target(scope, reference) is { } to) { if (to != id) b.Dependencies.Add(new(id, to, explicitDependency ? "depends_on" : "reference", ArchitectureEvidenceState.Confirmed, safeFile, attribute.Line)); }
                                else if (!reference.StartsWith("var.") && !reference.StartsWith("local.") && InfrastructureCatalog.KnownProvider(InfrastructureCatalog.Provider(reference.Split('.')[0]))
                                    && !InfrastructureCatalog.UtilityProvider(InfrastructureCatalog.Provider(reference.Split('.')[0])))
                                    b.Unresolved.Add($"{SourceEvidenceRedaction.Safe(reference)} (referenced in {safeFile}; not declared in this module)");
                            }
                        }
                        Access(b, id, type, block, scope, Resolve, Target);
                        break;
                    }
                    case "module" when block.Labels.Count == 1:
                    {
                        var source = block.Attribute("source") is { } s ? HclReader.Literal(s.Expression) : null;
                        var local = source is not null && (source.StartsWith("./") || source.StartsWith("../"));
                        var resolved = local ? Normalize(scope.Directory.Length == 0 ? source! : $"{scope.Directory}/{source}") : null;
                        var analyzed = resolved is not null && scopes.ContainsKey(resolved);
                        var id = Id(scope.Directory, $"module.{block.Labels[0]}");
                        b.Modules.Add(new InfrastructureModule
                        {
                            Id = id, Name = block.Labels[0], Source = source is null ? "(computed)" : SafeSource(source), Local = local, ResolvedPath = resolved, Analyzed = analyzed,
                            Version = block.Attribute("version") is { } v ? HclReader.Literal(v.Expression) : null,
                            Inputs = block.Attributes.Select(a => a.Name).Where(n => n is not ("source" or "version" or "count" or "for_each" or "providers" or "depends_on")).ToList(),
                            File = safeFile, Line = block.Line,
                            Note = local ? analyzed ? null : "Local module path not found in the selected source." : ExternalModule,
                        });
                        if (!local) b.Limitations.Add(ExternalModule);
                        foreach (var attribute in block.Attributes.Where(a => a.Name is not ("source" or "version")))
                            foreach (var reference in HclReader.References(attribute.Expression))
                                if (Target(scope, reference) is { } to)
                                {
                                    b.Dependencies.Add(new(id, to, attribute.Name == "depends_on" ? "depends_on" : "module-input", ArchitectureEvidenceState.Confirmed, safeFile, attribute.Line));
                                    // Module wiring: resources inside the module that read var.<input> depend on what the caller passes.
                                    if (analyzed && attribute.Name != "depends_on")
                                        foreach (var (_, inner) in scopes[resolved!].Blocks.Where(x => x.Block.Type == "resource" && x.Block.Labels.Count == 2))
                                            if (inner.AllAttributes().Any(x => HclReader.References(x.Attribute.Expression).Contains($"var.{attribute.Name}")))
                                                b.Dependencies.Add(new(Id(resolved!, $"{inner.Labels[0]}.{inner.Labels[1]}"), to, "module-wiring", ArchitectureEvidenceState.StronglySupported, safeFile, attribute.Line));
                                }
                        break;
                    }
                    case "variable" when block.Labels.Count == 1:
                    {
                        var sensitive = block.Attribute("sensitive") is { } s && HclReader.Literal(s.Expression) == "true";
                        var name = block.Labels[0];
                        var (state, safe) = block.Attribute("default") is not { } d ? ("none", (string?)null) : DefaultState(name, d.Expression, sensitive);
                        b.Variables.Add(new InfrastructureVariable
                        {
                            Name = SourceEvidenceRedaction.Safe(name), Type = block.Attribute("type") is { } t ? SourceEvidenceRedaction.Safe(t.Expression.Length > 60 ? t.Expression[..60] + "…" : t.Expression) : null,
                            Sensitive = sensitive, DefaultState = state, SafeDefault = safe, File = safeFile, Line = block.Line,
                        });
                        break;
                    }
                    case "locals":
                        b.Locals.AddRange(block.Attributes.Select(a => SourceEvidenceRedaction.Safe(a.Name)));
                        break;
                    case "output" when block.Labels.Count == 1:
                        b.Outputs.Add(new InfrastructureOutput(SourceEvidenceRedaction.Safe(block.Labels[0]), block.Attribute("sensitive") is { } os && HclReader.Literal(os.Expression) == "true",
                            block.Attribute("value") is { } value ? HclReader.References(value.Expression).Select(r => Target(scope, r) ?? SourceEvidenceRedaction.Safe(r)).Distinct().ToList() : [], safeFile, block.Line));
                        break;
                    case "provider" when block.Labels.Count >= 1:
                    {
                        var name = block.Labels[0];
                        var credentials = block.AllAttributes().Any(x => SourceEvidenceRedaction.SensitiveKey(x.Key) || x.Key is "client_id" or "client_certificate_path" or "access_key" or "secret_key" or "credentials");
                        b.Providers[name] = b.Providers.TryGetValue(name, out var existing)
                            ? existing with { HasCredentialSettings = existing.HasCredentialSettings || credentials }
                            : new InfrastructureProvider(name, null, null, credentials, safeFile, block.Line);
                        if (credentials) b.Diagnostics.Add(new("Provider credentials", $"Provider '{SourceEvidenceRedaction.Safe(name)}' declares credential settings in source; values are not read.", safeFile, block.Line));
                        break;
                    }
                    case "terraform":
                        foreach (var required in block.Nested("required_providers"))
                            foreach (var attribute in required.Attributes)
                            {
                                var src = Regex.Match(attribute.Expression, @"source\s*=\s*""([^""]+)""").Groups[1].Value;
                                var version = Regex.Match(attribute.Expression, @"version\s*=\s*""([^""]+)""").Groups[1].Value;
                                b.Providers[attribute.Name] = (b.Providers.TryGetValue(attribute.Name, out var existing) ? existing : new InfrastructureProvider(attribute.Name, null, null, false, safeFile, attribute.Line))
                                    with { Source = src.Length > 0 ? SourceEvidenceRedaction.Safe(src) : null, Version = version.Length > 0 ? SourceEvidenceRedaction.SafePath(version) : null };
                            }
                        foreach (var backend in block.Nested("backend").Where(x => x.Labels.Count == 1))
                            b.Backends.Add(new InfrastructureBackend(SourceEvidenceRedaction.Safe(backend.Labels[0]), backend.Attributes.Select(a => SourceEvidenceRedaction.Safe(a.Name)).ToList(), safeFile, backend.Line));
                        foreach (var cloud in block.Nested("cloud"))
                            b.Backends.Add(new InfrastructureBackend("Terraform Cloud", cloud.AllAttributes().Select(x => SourceEvidenceRedaction.Safe(x.Key)).ToList(), safeFile, cloud.Line));
                        break;
                }
            }
        }

        // tfvars: environment-specific variable values. Sensitive variables, secret-shaped names and values are never read out.
        foreach (var file in files.Where(f => f.Technology == "Terraform variables"))
        {
            var root = HclReader.Parse(file.Content, out var errors);
            if (errors.Count > 0) b.Diagnostics.Add(new("Parse error", "tfvars file could not be fully read.", SourceEvidenceRedaction.Safe(file.Path)));
            var env = SourceFileClassifier.EnvironmentFromName(file.Name) ?? FolderEnvironment(file.Path) ?? SourceEnvironmentLabel.Default;
            var basis = SourceFileClassifier.EnvironmentFromName(file.Name) is not null ? "tfvars file name" : FolderEnvironment(file.Path) is not null ? "folder name" : "default tfvars";
            b.Environment(env, file.Path, basis);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in root.Attributes)
            {
                // A tfvars file sets the root module beside it; variables of other (module) directories with the same name are not its targets.
                var sameDir = b.Variables.Where(v => v.Name == attribute.Name && Dir(v.File) == SourceEvidenceRedaction.Safe(file.Directory)).ToList();
                var declared = sameDir.Count > 0 ? sameDir : b.Variables.Where(v => v.Name == attribute.Name && !moduleDirs.Contains(Dir(v.File))).ToList();
                var sensitive = declared.Any(v => v.Sensitive) || SourceEvidenceRedaction.SensitiveKey(attribute.Name);
                var literal = HclReader.Literal(attribute.Expression);
                var (state, safe) = DefaultState(attribute.Name, attribute.Expression, sensitive);
                var value = new InfrastructureVariableValue(env, SourceEvidenceRedaction.Safe(file.Path), attribute.Line, state, safe);
                if (declared.Count == 0)
                    b.Variables.Add(new InfrastructureVariable { Name = SourceEvidenceRedaction.Safe(attribute.Name), Type = "(not declared in analyzed .tf)", DefaultState = "none", File = SourceEvidenceRedaction.Safe(file.Path), Line = attribute.Line, Values = [value] });
                else foreach (var v in declared) v.Values.Add(value);
                b.Pending.Add(new(file.Path, attribute.Line, attribute.Name, literal ?? "", "Terraform variables", env, sensitive));
                if (literal is not null && !sensitive) values[attribute.Name] = literal;
            }
            if (env.Kind == SourceEnvironmentKind.Default || values.Count == 0) continue;
            // Resource names in this environment: the root module beside the tfvars file, else every root module (never child modules).
            overrides = values;
            var dirScopes = names.Where(n => n.Scope.Directory == file.Directory).ToList();
            foreach (var (scope, index, expression) in dirScopes.Count > 0 ? dirScopes : names)
                if (Resolve(scope, expression) is { } name && SourceEvidenceRedaction.SafeLiteral("name", name) is { } safeName)
                    b.Resources[index] = b.Resources[index] with
                    {
                        EnvironmentNames = [.. b.Resources[index].EnvironmentNames.Where(e => e.Environment != env), new InfrastructureEnvironmentName(env, safeName, $"tfvars {SourceEvidenceRedaction.Safe(file.Path)}")],
                    };
            overrides = null;
        }
    }

    private static (string State, string? Safe) DefaultState(string name, string expression, bool sensitive)
    {
        var literal = HclReader.Literal(expression);
        if (literal is null) return ("expression", null);
        if (sensitive || SourceEvidenceRedaction.SensitiveKey(name) || SourceEvidenceRedaction.SecretShaped(literal) || SourceEvidenceRedaction.ConnectionShaped(literal)) return ("redacted", null);
        return SourceEvidenceRedaction.SafeLiteral(name, literal) is { } safe ? ("literal", safe) : ("redacted", null);
    }

    private static string SafeSource(string source)
    {
        var s = Regex.Replace(source, @"\?.*$", "");
        s = Regex.Replace(s, @"//[^/@]+@", "//");
        return SourceEvidenceRedaction.Safe(s);
    }

    private static List<InfrastructureSetting> Settings(TfModuleScope scope, HclBlock block, Func<TfModuleScope, string, int, string?> resolve, Func<TfModuleScope, string, string?> target)
    {
        var settings = new List<InfrastructureSetting>();
        foreach (var (key, attribute) in block.AllAttributes())
        {
            if (key == "app_settings" || key == "site_config.app_settings")
            {
                foreach (var appKey in HclReader.ObjectKeys(attribute.Expression).Take(60))
                    settings.Add(new($"app_settings.{SourceEvidenceRedaction.Safe(appKey)}", SourceEvidenceRedaction.SensitiveKey(appKey) ? "[sensitive — value not shown]" : "(declared)", "Configuration", false));
                continue;
            }
            if (InfrastructureCatalog.SettingArea(key) is not { } area) continue;
            settings.Add(new(SourceEvidenceRedaction.Safe(key), SettingValue(scope, key, attribute.Expression, resolve, target, out var resolved), area, resolved));
        }
        // Nested blocks that matter by presence (identity, cors, private_service_connection, ingress rules, diagnostic categories).
        foreach (var nested in block.Blocks.Where(n => n.Type is "identity" or "cors" or "private_service_connection" or "network_rules" or "ip_restriction" or "auth_settings" or "auth_settings_v2" or "ingress" or "egress" or "enabled_log" or "log" or "metric"))
            if (!settings.Any(s => s.Key.StartsWith(nested.Type + ".", StringComparison.Ordinal)))
                settings.Add(new($"{nested.Type} block", "declared", InfrastructureCatalog.SettingArea(nested.Type + ".enabled") ?? (nested.Type is "identity" ? "Identity" : nested.Type is "enabled_log" or "log" or "metric" ? "Observability" : "Networking"), true));
        return settings.DistinctBy(s => (s.Key, s.Value)).Take(80).ToList();
    }

    private static string SettingValue(TfModuleScope scope, string key, string expression, Func<TfModuleScope, string, int, string?> resolve, Func<TfModuleScope, string, string?> target, out bool resolved)
    {
        resolved = false;
        if (SourceEvidenceRedaction.SensitiveKey(key)) return "[sensitive — value not shown]";
        if (HclReader.IsHeredoc(expression)) return "(multi-line value)";
        var value = resolve(scope, expression, 0);
        if (value is not null)
        {
            var safe = SourceEvidenceRedaction.SafeLiteral(key, value);
            resolved = safe is not null;
            return safe ?? "[sensitive — value not shown]";
        }
        var e = expression.Trim();
        if (e.StartsWith('['))
        {
            var items = HclReader.References(e).ToList();
            var count = Regex.Matches(e, @"""[^""]*""|[A-Za-z_][\w.\-]*\.[\w.\-]+").Count;
            return items.Count > 0 ? $"(list of references: {string.Join(", ", items.Select(r => target(scope, r) ?? SourceEvidenceRedaction.Safe(r)).Distinct().Take(4))})" : $"(list, {count} item(s))";
        }
        var references = HclReader.References(e).ToList();
        if (references.Count == 1 && target(scope, references[0]) is { } to) return $"→ {to}";
        if (references.Count == 1 && references[0].StartsWith("var.")) return $"(variable {SourceEvidenceRedaction.Safe(references[0])})";
        return references.Count > 0 ? $"(expression referencing {string.Join(", ", references.Select(SourceEvidenceRedaction.Safe).Distinct().Take(3))})" : "(expression)";
    }

    private static void Access(Builder b, string id, string type, HclBlock block, TfModuleScope scope, Func<TfModuleScope, string, int, string?> resolve, Func<TfModuleScope, string, string?> target)
    {
        string? Ref(string attribute) => block.Attribute(attribute) is { } a
            ? HclReader.References(a.Expression).Select(r => target(scope, r) ?? SourceEvidenceRedaction.Safe(r)).FirstOrDefault() ?? (resolve(scope, a.Expression, 0) is { } v ? SourceEvidenceRedaction.SafeLiteral(attribute, v) : "(expression)")
            : null;
        string? Literal(string attribute) => block.Attribute(attribute) is { } a && resolve(scope, a.Expression, 0) is { } v ? SourceEvidenceRedaction.SafeLiteral(attribute, v) : null;
        AccessAssignment? assignment = type switch
        {
            "azurerm_role_assignment" => new() { ResourceId = id, Role = Literal("role_definition_name") ?? Ref("role_definition_id"), ScopeReference = Ref("scope"), PrincipalReference = Ref("principal_id") },
            "azurerm_key_vault_access_policy" => new() { ResourceId = id, Role = "Key Vault access policy", ScopeReference = Ref("key_vault_id"), PrincipalReference = Ref("object_id") },
            "aws_iam_role_policy_attachment" => new() { ResourceId = id, Role = Literal("policy_arn") ?? Ref("policy_arn"), ScopeReference = null, PrincipalReference = Ref("role") },
            _ when type.StartsWith("google_", StringComparison.Ordinal) && (type.EndsWith("_iam_member", StringComparison.Ordinal) || type.EndsWith("_iam_binding", StringComparison.Ordinal))
                => new() { ResourceId = id, Role = Literal("role"), ScopeReference = type, PrincipalReference = Literal("member") ?? Ref("member") ?? "(members list)" },
            "kubernetes_role_binding" or "kubernetes_cluster_role_binding" => new() { ResourceId = id, Role = block.Nested("role_ref").FirstOrDefault()?.Attribute("name") is { } n ? HclReader.Literal(n.Expression) : null, PrincipalReference = "(subjects)" },
            _ => null,
        };
        if (assignment is not null) b.Access.Add(assignment with { EvidenceState = ArchitectureEvidenceState.Confirmed });
    }

    // ── Bicep (pattern-based, Partial) ──────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex BicepResource = new(@"(?m)^\s*resource\s+([A-Za-z_]\w*)\s+'([^'@]+)@([^']+)'\s*(existing\s*)?=", RegexOptions.Compiled);
    private static readonly Regex BicepParam = new(@"(?m)^(\s*@secure\(\)\s*\n)?\s*param\s+([A-Za-z_]\w*)\s+([A-Za-z_]\w*)(\s*=\s*(.+))?$", RegexOptions.Compiled);
    private static readonly Regex BicepModule = new(@"(?m)^\s*module\s+([A-Za-z_]\w*)\s+'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex BicepOutput = new(@"(?m)^\s*output\s+([A-Za-z_]\w*)\s+\w+", RegexOptions.Compiled);
    private static readonly Regex BicepSetting = new(@"(?m)^\s*([A-Za-z_]\w*)\s*:\s*('([^'$]*)'|true|false|-?\d+)\s*$", RegexOptions.Compiled);

    private static string Snake(string camel) => Regex.Replace(camel, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    private static void Bicep(Builder b, List<EvidenceFile> files, CancellationToken ct)
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

    private static void Arm(Builder b, List<EvidenceFile> files, CancellationToken ct)
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

    private static void Kubernetes(Builder b, List<EvidenceFile> files, CancellationToken ct)
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

    private static void Helm(Builder b, List<EvidenceFile> files)
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
