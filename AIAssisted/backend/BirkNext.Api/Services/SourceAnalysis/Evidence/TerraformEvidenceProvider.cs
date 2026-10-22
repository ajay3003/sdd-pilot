using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using static BirkNext.Api.Services.SourceAnalysis.Evidence.InfrastructureEvidenceBuilder;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Infrastructure evidence provider: Terraform. HCL is read structurally (blocks, attributes, references, local modules, variables, tfvars,
/// outputs, providers, required_version, lock files, backends, remote state); nothing is evaluated beyond literals, variables and locals, and
/// nothing is executed — no terraform init/plan/apply, no module or provider download, no state, no backend.
/// </summary>
internal sealed partial class TerraformEvidenceProvider : IInfrastructureSourceEvidenceProvider
{
    // ── Terraform ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record TfModuleScope(string Directory, List<(EvidenceFile File, HclBlock Block)> Blocks)
    {
        public Dictionary<string, (string Default, bool Sensitive)> Defaults { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> LocalExpressions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ResourceIds { get; } = new(StringComparer.Ordinal);
    }


    /// <summary>Module inputs that name the deployed resource, in order of preference.</summary>
    private static readonly string[] DeploymentNameInputs = ["container_app_name", "app_name", "function_app_name", "web_app_name", "service_name", "name"];

    /// <summary>
    /// The deployment name an input expression states, as a template: literals and resolvable variables/locals verbatim, every other part
    /// '*' (a per-environment variable, a computed value). "local.names.x" reads key x of a local object. Null when nothing literal remains.
    /// </summary>
    private static string? NameTemplate(TfModuleScope scope, string expression, Func<TfModuleScope, string, int, string?> resolve, int depth = 0)
    {
        if (depth > 3) return null;
        var e = expression.Trim();
        if (resolve(scope, e, 0) is { } literal) return Template(literal);
        var member = Regex.Match(e, @"^local\.([A-Za-z_][A-Za-z0-9_\-]*)\.([A-Za-z_][A-Za-z0-9_\-]*)$");
        if (member.Success && scope.LocalExpressions.TryGetValue(member.Groups[1].Value, out var obj)
            && Regex.Match(obj, $@"(?m)^\s*""?{Regex.Escape(member.Groups[2].Value)}""?\s*=\s*(.+?)\s*$") is { Success: true } value)
            return NameTemplate(scope, value.Groups[1].Value, resolve, depth + 1);
        var single = Regex.Match(e, @"^local\.([A-Za-z_][A-Za-z0-9_\-]*)$");
        if (single.Success && scope.LocalExpressions.TryGetValue(single.Groups[1].Value, out var local)) return NameTemplate(scope, local, resolve, depth + 1);
        if (e.Length < 2 || e[0] != '"' || e[^1] != '"') return null;
        var text = Regex.Replace(e[1..^1], @"\$\{([^}]*)\}", m => resolve(scope, m.Groups[1].Value.Trim(), 0) ?? "*");
        return Template(text);

        static string? Template(string value)
        {
            var collapsed = Regex.Replace(value, @"\*+", "*");
            // A deployment name is an identifier: letters, digits, '.', '-', '_' and the '*' wildcard only (anything else is not kept).
            return collapsed.Count(char.IsLetterOrDigit) >= 3 && collapsed.Length <= 120 && Regex.IsMatch(collapsed, @"^[A-Za-z0-9._*\-]+$") ? collapsed : null;
        }
    }

    /// <summary>
    /// Endpoint settings in an object expression that read another declaration's address: <c>"X__BaseUrl" = "https://${module.y.fqdn}"</c> or
    /// <c>X_URL = module.y.uri</c>. Keys are returned in ':' form; the target is the module/resource reference and the member it reads.
    /// </summary>
    private static IEnumerable<(string Key, string Target, string Member)> EndpointSettings(string expression)
    {
        foreach (Match entry in Regex.Matches(expression, @"(?m)^\s*[{,]?\s*""?([A-Za-z_][A-Za-z0-9_\-.:]*)""?\s*=\s*(.+?)\s*,?\s*$"))
        {
            var key = entry.Groups[1].Value.Replace("__", ":");
            if (!EndpointKey.IsMatch(key)) continue;
            foreach (var reference in HclReader.References(entry.Groups[2].Value))
            {
                var parts = reference.Split('.');
                if (parts.Length < 2 || parts[0] is "var" or "local" or "data") continue;
                var member = parts[0] == "module" ? parts.Length >= 3 ? parts[2] : ""
                    : Regex.Match(entry.Groups[2].Value, $@"{Regex.Escape(reference)}\.([A-Za-z_][A-Za-z0-9_]*)").Groups[1].Value;
                if (member.Length == 0 || !AddressMember.IsMatch(member)) continue;
                yield return (key, reference, member);
            }
        }
    }

    private static readonly Regex EndpointKey = new(@"(?i)(base_?url|baseaddress|url|uri|endpoint|host|fqdn|address)$", RegexOptions.CultureInvariant);
    private static readonly Regex AddressMember = new(@"(?i)(fqdn|url|uri|host|endpoint|address|hostname)", RegexOptions.CultureInvariant);

    private static void Terraform(InfrastructureEvidenceBuilder b, List<EvidenceFile> files, CancellationToken ct)
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
                        if (!(block.Type == "data" && type == "terraform_remote_state") && !InfrastructureCatalog.KnownProvider(provider))
                            b.Diagnostics.Add(new("Unsupported provider", $"Provider '{SourceEvidenceRedaction.Safe(provider)}' has no category adapter; its resources are recorded generically.", safeFile, block.Line));
                        var nameAttribute = block.Attribute("name") ?? block.Attribute("bucket") ?? block.Nested("metadata").FirstOrDefault()?.Attribute("name");
                        var declared = nameAttribute is null ? null : Resolve(scope, nameAttribute.Expression);
                        // Only names built from variables/locals vary per environment; a literal name is environment-independent.
                        if (nameAttribute is not null && modulePath is null && block.Type == "resource" && HclReader.References(nameAttribute.Expression).Any(r => r.StartsWith("var.", StringComparison.Ordinal) || r.StartsWith("local.", StringComparison.Ordinal)))
                            names.Add((scope, b.Resources.Count, nameAttribute.Expression));
                        var safeName = declared is null ? null : SourceEvidenceRedaction.SafeLiteral("name", declared);
                        var instancing = block.Attribute("count") ?? block.Attribute("for_each");
                        b.Resources.Add(new InfrastructureResource
                        {
                            InstanceExpression = instancing is null ? null : InstanceText(instancing), Conditional = instancing is not null && IsConditional(instancing),
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
                            DeploymentName = DeploymentNameInputs.Select(block.Attribute).FirstOrDefault(a => a is not null) is { } nameInput ? NameTemplate(scope, nameInput.Expression, Resolve) : null,
                        });
                        foreach (var attribute in block.Attributes.Where(a => a.Name is not ("source" or "version" or "depends_on")))
                            foreach (var (key, target, member) in EndpointSettings(attribute.Expression))
                                if (Target(scope, target) is { } to && to != id)
                                    b.Injections.Add(new(id, SourceEvidenceRedaction.Safe(key), to, SourceEvidenceRedaction.Safe(member), safeFile, attribute.Line));
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
                        if (block.Attribute("required_version") is { } requiredVersion && HclReader.Literal(requiredVersion.Expression) is { } constraint)
                            b.RequiredVersions.Add(new(SourceEvidenceRedaction.SafePath(constraint), SourceEvidenceRedaction.Safe(scope.Directory), safeFile, requiredVersion.Line));
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

        RemoteStates(b, scopes, moduleDirs);

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

    private static void Access(InfrastructureEvidenceBuilder b, string id, string type, HclBlock block, TfModuleScope scope, Func<TfModuleScope, string, int, string?> resolve, Func<TfModuleScope, string, string?> target)
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
}
