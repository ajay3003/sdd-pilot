using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SetupProfiles;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.TestData;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExternalIntegrations;

/// <summary>One system in a package: provider, name and the provider's portable settings only.</summary>
public sealed record SetupPackageSystem(string Name, ExternalProviderType Provider, string? EnvironmentName, bool Enabled, IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// The generic, versioned environment setup package (schema v1): metadata, scope, systems (provider sections), test packs and an explicit
/// dependency manifest. It is reusable intent, never the source of truth: after Apply the canonical owners keep the values.
/// </summary>
public sealed record SetupPackageModel
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "1";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public SetupScope Scope { get; init; } = SetupScope.Environment;
    public ExternalProviderType? Provider { get; init; }
    public string? EnvironmentType { get; init; }
    public IReadOnlyList<string> TestPacks { get; init; } = [];
    public IReadOnlyList<SetupPackageSystem> Systems { get; init; } = [];
    public IReadOnlyList<SetupDependency> Dependencies { get; init; } = [];
}

/// <summary>
/// Parse → validate the environment setup package with the setup-profile parser and its safety rules: secret, identity and trust keys are
/// rejected (never echoed), credential- or identifier-looking values are rejected, non-portable provider fields are rejected, unknown
/// providers are an error. Never mutates anything.
/// </summary>
public static class SetupPackageSchema
{
    public const int SupportedVersion = 1;
    private static readonly string[] RootKeys = ["schemaVersion", "package", "testPacks", "systems", "dependencies", "metadata"];
    private static readonly string[] PackageKeys = ["id", "version", "name", "description", "scope", "provider", "environmentType"];
    private static readonly string[] SystemKeys = ["name", "provider", "environment", "enabled", "settings"];
    private static readonly string[] DependencyKeys = ["kind", "reference", "role", "provider"];

    public static (SetupPackageModel? Model, IReadOnlyList<SetupProfileIssue> Issues) Parse(string? fileName, string content)
    {
        var (root, _, parseIssues) = SetupProfileParser.Parse(fileName ?? "setup.yaml", content);
        var issues = new List<SetupProfileIssue>(parseIssues);
        if (root is null || !root.IsMap) { if (issues.Count == 0) issues.Add(new("", SetupIssueSeverity.Error, "The package is not a YAML/JSON map.")); return (null, issues); }
        Safety(root, issues);

        var version = root.Str("schemaVersion");
        if (version is null) issues.Add(new("schemaVersion", SetupIssueSeverity.Error, "The package has no schemaVersion; an unversioned package is never accepted."));
        else if (!int.TryParse(version, out var number) || number != SupportedVersion) issues.Add(new("schemaVersion", SetupIssueSeverity.Error, $"Package schema \"{SetupProfileSchema.Safe(version)}\" is not supported (supported: {SupportedVersion})."));
        if (root["metadata"]?.Str("containsSecrets") is { } contains && contains.Equals("true", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("metadata.containsSecrets", SetupIssueSeverity.Error, "A package that declares it contains secrets is never imported."));
        foreach (var (key, _) in root.Map!) if (!RootKeys.Contains(key, StringComparer.OrdinalIgnoreCase) && Usable(key, issues)) issues.Add(new(key, SetupIssueSeverity.Warning, $"Unsupported property \"{key}\" is ignored."));

        var package = root["package"];
        if (package is not { IsMap: true }) { issues.Add(new("package", SetupIssueSeverity.Error, "The package has no \"package\" section (name, scope).")); return (null, issues); }
        Unknown(package, PackageKeys, issues);
        var name = package.Str("name")?.Trim();
        if (string.IsNullOrEmpty(name)) issues.Add(new("package.name", SetupIssueSeverity.Error, "The package has no name."));
        var scope = SetupScope.Environment;
        if (package.Str("scope") is { } scopeText && !Enum.TryParse(scopeText, true, out scope)) issues.Add(new("package.scope", SetupIssueSeverity.Error, $"\"{SetupProfileSchema.Safe(scopeText)}\" is not a setup scope (Integration, Provider, Environment)."));
        ExternalProviderType? provider = null;
        if (package.Str("provider") is { } providerText)
        {
            if (ExternalProviderCatalog.TryParse(providerText, out var parsed)) provider = parsed;
            else issues.Add(new("package.provider", SetupIssueSeverity.Error, $"Unsupported provider \"{SetupProfileSchema.Safe(providerText)}\"."));
        }

        var systems = new List<SetupPackageSystem>();
        foreach (var system in root["systems"]?.Items ?? [])
        {
            if (!system.IsMap) { issues.Add(new(system.Path, SetupIssueSeverity.Error, "A system must be a map.")); continue; }
            Unknown(system, SystemKeys, issues);
            var systemName = system.Str("name")?.Trim();
            if (string.IsNullOrEmpty(systemName)) { issues.Add(new($"{system.Path}.name", SetupIssueSeverity.Error, "A system has no name.")); continue; }
            if (!ExternalProviderCatalog.TryParse(system.Str("provider"), out var type))
            { issues.Add(new($"{system.Path}.provider", SetupIssueSeverity.Error, $"Unsupported provider \"{SetupProfileSchema.Safe(system.Str("provider") ?? "")}\" for {systemName}.")); continue; }
            var descriptor = ExternalProviderCatalog.Descriptor(type);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in system["settings"]?.Map ?? [])
            {
                var path = $"{system.Path}.settings.{key}";
                if (!Usable(path, issues)) continue;
                var field = descriptor.Fields.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
                if (field is null) { issues.Add(new(path, SetupIssueSeverity.Warning, $"\"{key}\" is not a {descriptor.DisplayName} setting and is ignored.")); continue; }
                if (field.Secret || !field.Portable) { issues.Add(new(path, SetupIssueSeverity.Rejected, $"{field.Label} is not portable: it is re-established at the destination, never imported.")); continue; }
                var text = value.Scalar ?? (value.Items is { } list ? string.Join(" ", list.Select(item => item.Scalar).OfType<string>()) : null);
                if (text is null) continue;
                if (ProviderFieldValidation.Problem(field, text) is { } problem) { issues.Add(new(path, SetupIssueSeverity.Error, problem)); continue; }
                settings[field.Key] = text.Trim();
            }
            systems.Add(new(systemName, type, system.Str("environment")?.Trim(), !string.Equals(system.Str("enabled"), "false", StringComparison.OrdinalIgnoreCase), settings));
        }

        var dependencies = new List<SetupDependency>();
        foreach (var (section, required) in new[] { ("required", true), ("optional", false) })
            foreach (var item in root["dependencies"]?[section]?.Items ?? [])
            {
                if (!item.IsMap) continue;
                Unknown(item, DependencyKeys, issues);
                if (!Enum.TryParse<SetupDependencyKind>(item.Str("kind"), true, out var kind)) { issues.Add(new($"{item.Path}.kind", SetupIssueSeverity.Error, "Unknown dependency kind.")); continue; }
                if (item.Str("reference")?.Trim() is not { Length: > 0 } reference || !Usable($"{item.Path}.reference", issues)) { issues.Add(new($"{item.Path}.reference", SetupIssueSeverity.Error, "A dependency needs a reference.")); continue; }
                dependencies.Add(new(kind, reference, required, item.Str("role")?.Trim(), item.Str("provider")?.Trim()));
            }

        var testPacks = (root["testPacks"]?.Items ?? []).Select(item => item.Scalar?.Trim()).OfType<string>().Where(item => item.Length > 0).ToList();
        if (systems.Count == 0 && issues.All(issue => issue.Severity != SetupIssueSeverity.Error)) issues.Add(new("systems", SetupIssueSeverity.Error, "The package configures no system."));
        if (scope != SetupScope.Environment && systems.Select(item => item.Provider).Distinct().Count() > 1)
            issues.Add(new("systems", SetupIssueSeverity.Error, $"A {scope.ToString().ToLowerInvariant()}-scoped package must contain one provider."));
        if (scope == SetupScope.Integration && systems.Count > 1) issues.Add(new("systems", SetupIssueSeverity.Error, "An integration-scoped package contains exactly one system."));
        return (new SetupPackageModel
        {
            Id = package.Str("id")?.Trim() ?? "", Version = package.Str("version")?.Trim() ?? "1", Name = name ?? "", Description = package.Str("description")?.Trim(),
            Scope = scope, Provider = provider ?? (systems.Select(item => item.Provider).Distinct().Count() == 1 && scope != SetupScope.Environment ? systems[0].Provider : null),
            EnvironmentType = package.Str("environmentType")?.Trim(), TestPacks = testPacks, Systems = systems, Dependencies = dependencies,
        }, issues);
    }

    private static void Unknown(SetupNode node, string[] known, List<SetupProfileIssue> issues)
    {
        foreach (var (key, _) in node.Map ?? [])
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase) && Usable(SetupProfileParser.Join(node.Path, key), issues))
                issues.Add(new(SetupProfileParser.Join(node.Path, key), SetupIssueSeverity.Warning, $"Unsupported property \"{key}\" is ignored."));
    }

    /// <summary>The profile safety rules on every key and value of the package (secret, identity, trust; credential/identifier-looking values).</summary>
    private static void Safety(SetupNode node, List<SetupProfileIssue> issues)
    {
        if (node.IsList) { foreach (var item in node.Items!) Safety(item, issues); return; }
        if (!node.IsMap) { if (SetupProfileSchema.ValueIssue(node) is { } issue) issues.Add(issue); return; }
        foreach (var (key, value) in node.Map!)
        {
            var path = SetupProfileParser.Join(node.Path, key);
            // "enabled" is a portable switch of the system itself, not trust; every other trust/secret/identity key is rejected.
            if (!key.Equals("enabled", StringComparison.OrdinalIgnoreCase) && SetupProfileSchema.KeyIssue(key, path) is { } issue) { issues.Add(issue); continue; }
            Safety(value, issues);
        }
    }

    private static bool Usable(string path, IReadOnlyList<SetupProfileIssue> issues) => SetupProfileSchema.Usable(path, issues);

    /// <summary>The package as a map the YAML emitter and JSON serializer both write; the parser reads it back (round trip).</summary>
    public static Dictionary<string, object?> ToDocument(SetupPackageModel model, DateTimeOffset generatedAt)
    {
        Dictionary<string, object?> Dep(SetupDependency d)
        {
            var map = new Dictionary<string, object?> { ["kind"] = d.Kind.ToString(), ["reference"] = d.Reference };
            if (d.Role is not null) map["role"] = d.Role;
            if (d.Provider is not null) map["provider"] = d.Provider;
            return map;
        }
        var package = new Dictionary<string, object?> { ["id"] = model.Id, ["version"] = model.Version, ["name"] = model.Name };
        if (model.Description is not null) package["description"] = model.Description;
        package["scope"] = model.Scope.ToString();
        if (model.Provider is { } provider) package["provider"] = provider.ToString();
        if (model.EnvironmentType is not null) package["environmentType"] = model.EnvironmentType;
        var document = new Dictionary<string, object?> { ["schemaVersion"] = SupportedVersion.ToString(), ["package"] = package };
        if (model.TestPacks.Count > 0) document["testPacks"] = model.TestPacks.Cast<object?>().ToList();
        document["systems"] = model.Systems.Select(system =>
        {
            var map = new Dictionary<string, object?> { ["name"] = system.Name, ["provider"] = system.Provider.ToString() };
            if (system.EnvironmentName is not null) map["environment"] = system.EnvironmentName;
            map["enabled"] = system.Enabled ? "true" : "false";
            map["settings"] = system.Settings.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary(entry => entry.Key, entry => (object?)entry.Value);
            return (object?)map;
        }).ToList();
        document["dependencies"] = new Dictionary<string, object?>
        {
            ["required"] = model.Dependencies.Where(d => d.Required).Select(d => (object?)Dep(d)).ToList(),
            ["optional"] = model.Dependencies.Where(d => !d.Required).Select(d => (object?)Dep(d)).ToList(),
        };
        document["metadata"] = new Dictionary<string, object?>
        {
            ["generatedBy"] = "BirkNext", ["generatedAt"] = generatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["sanitized"] = "true", ["containsSecrets"] = "false",
        };
        return document;
    }

    public static string Serialize(SetupPackageModel model, SetupPackageFormat format, DateTimeOffset generatedAt)
    {
        var document = ToDocument(model, generatedAt);
        return format == SetupPackageFormat.Json
            ? JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true })
            : "# BirkNext environment setup package — sanitized: no secret, raw identity, trust or runtime evidence.\n" + SetupProfileService.Yaml(document, 0);
    }

    public static readonly IReadOnlyList<string> Excluded =
    [
        "Secrets (client secret, connection strings, passwords) — added for the session only at the destination",
        "Raw synthetic identities — Test Data aliases and roles only",
        "Trust, execution allowed and app-policy confirmation — re-established by an administrator at the destination",
        "Runtime verification, runtime observations and browser login evidence",
        "Tokens, cookies, authorization codes, state and nonce",
    ];
}

/// <summary>
/// The one setup pipeline for files and the Setup Library: Build/Export from canonical owners → Parse/validate → provider compatibility →
/// resolve every dependency against its owner → conflicts → complete change plan (no write) → Apply all-or-nothing. A required dependency
/// that is Missing, Ambiguous, Incompatible or Not approved blocks Apply; nothing is created as a placeholder and nothing is chosen for you.
/// </summary>
public sealed class EnvironmentSetupService(AppDbContext db, ExternalIntegrationService integrations, IIntegrationCatalogService catalog, TestDataCatalog testData,
    IReviewSourceEvidenceProvider sources, IEnumerable<IIntegrationJourneyPack> packs, TimeProvider clock)
{
    public async Task<(SetupPackageModel? Model, string? Problem)> BuildAsync(string environmentId, SetupScope scope, ExternalProviderType? provider, string? integrationId,
        string? name, string? description, IReadOnlyList<string>? testPacks, CancellationToken ct)
    {
        var all = await integrations.ListAsync(environmentId, ct);
        var selected = scope switch
        {
            SetupScope.Integration => all.Where(item => item.Id == integrationId).ToList(),
            SetupScope.Provider => all.Where(item => item.ProviderType == provider).ToList(),
            _ => all.ToList(),
        };
        if (scope == SetupScope.Integration && selected.Count == 0) return (null, "The integration does not exist in this environment.");
        if (scope == SetupScope.Provider && provider is null) return (null, "Choose a provider for a provider-scoped export.");
        if (selected.Count == 0) return (null, "There is nothing to export for this scope.");

        var systems = new List<SetupPackageSystem>();
        var dependencies = new List<SetupDependency>();
        foreach (var item in selected)
        {
            var handler = integrations.Handler(item.ProviderType);
            var current = await handler.CurrentSettingsAsync(item, ct);
            var descriptor = ExternalProviderCatalog.Descriptor(item.ProviderType);
            // Each provider decides what is portable; secrets and environment-local switches never leave.
            var portable = current.Where(entry => descriptor.Fields.Any(field => field.Key == entry.Key && field.Portable && !field.Secret))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            systems.Add(new(item.Name, item.ProviderType, item.EnvironmentName, item.Enabled, portable));
            dependencies.AddRange(await handler.SetupRequirementsAsync(item, portable, ct));
        }
        foreach (var pack in testPacks ?? []) dependencies.AddRange(TestPackSetupRequirements.For(pack));
        // A readable default name (never the environment's internal id): "Environment setup", "Altinn setup" or the integration's name.
        var packageName = string.IsNullOrWhiteSpace(name)
            ? scope switch { SetupScope.Environment => "Environment setup", SetupScope.Provider => $"{ExternalProviderCatalog.Descriptor(provider!.Value).DisplayName} setup", _ => $"{selected[0].Name} setup" }
            : name.Trim();
        return (new SetupPackageModel
        {
            Id = new string([.. packageName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-'), Name = packageName, Description = description?.Trim(),
            Scope = scope, Provider = scope == SetupScope.Environment ? null : selected[0].ProviderType, TestPacks = testPacks ?? [],
            Systems = systems, Dependencies = [.. dependencies.DistinctBy(d => (d.Kind, d.Reference, d.Role))],
        }, null);
    }

    public async Task<(SetupPackageExport? Export, string? Problem)> ExportAsync(string environmentId, SetupScope scope, ExternalProviderType? provider, string? integrationId,
        SetupPackageFormat format, IReadOnlyList<string>? testPacks, CancellationToken ct)
    {
        var (model, problem) = await BuildAsync(environmentId, scope, provider, integrationId, null, null, testPacks, ct);
        if (model is null) return (null, problem);
        var text = SetupPackageSchema.Serialize(model, format, clock.GetUtcNow());
        return (new SetupPackageExport(format, $"{model.Id}.setup.{(format == SetupPackageFormat.Json ? "json" : "yaml")}", text, SetupPackageSchema.Excluded, model.Dependencies), null);
    }

    /// <summary>The complete change plan and dependency resolution for this destination. Never writes.</summary>
    public async Task<SetupPreflightResult> PreflightAsync(string environmentId, SetupPreflightRequest request, CancellationToken ct)
    {
        var (model, parseIssues) = SetupPackageSchema.Parse(request.FileName, request.Content ?? "");
        var issues = parseIssues.ToList();
        if (model is null) return new SetupPreflightResult { Valid = false, Issues = issues, BlockReasons = ["The package could not be read."] };

        // Provider/scope compatibility with what the person selected (a mismatch is Incompatible; nothing is attempted).
        if (request.Scope is { } scope && scope != model.Scope)
            issues.Add(new("package.scope", SetupIssueSeverity.Error, $"Incompatible package: it is {model.Scope}-scoped, not {scope}-scoped."));
        if (request.Provider is { } provider && model.Systems.Any(system => system.Provider != provider))
            issues.Add(new("systems", SetupIssueSeverity.Error, $"Incompatible package: it configures {string.Join(", ", model.Systems.Select(s => s.Provider).Distinct())}, not {provider}."));

        var existing = await integrations.ListAsync(environmentId, ct);
        var changes = new List<SetupSystemChange>();
        var merged = new List<(ExternalProviderType Provider, IReadOnlyDictionary<string, string> Settings)>();
        foreach (var (system, index) in model.Systems.Select((system, index) => (system, index)))
        {
            var destination = Destination(system, existing, request);
            if (request.IntegrationId is not null && destination is null)
                issues.Add(new($"systems[{index}]", SetupIssueSeverity.Error, "The selected integration does not exist in this environment."));
            if (destination is not null && destination.ProviderType != system.Provider)
                issues.Add(new($"systems[{index}]", SetupIssueSeverity.Error, $"Incompatible package: {destination.Name} is {destination.ProviderType}, the package section is {system.Provider}."));
            var handler = integrations.Handler(system.Provider);
            foreach (var problem in handler.Validate(system.Settings)) issues.Add(new($"systems[{index}].settings", SetupIssueSeverity.Error, problem));
            var current = destination is null ? new Dictionary<string, string>() : await handler.CurrentSettingsAsync(destination, ct);
            var label = destination?.Name ?? system.Name;
            foreach (var (key, value) in system.Settings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var now = current.GetValueOrDefault(key);
                var classification = now is null ? "New" : string.Equals(now, value, StringComparison.Ordinal) ? "Same" : "Conflict";
                var fieldLabel = ExternalProviderCatalog.Descriptor(system.Provider).Fields.FirstOrDefault(field => field.Key == key)?.Label ?? key;
                changes.Add(new($"{index}:{key}", label, system.Provider, fieldLabel, now, value, classification,
                    classification switch { "Conflict" => "Current value differs: keep current or apply the package value (an explicit decision).", "Same" => "Already configured.", _ => destination is null ? "New system." : "Not configured yet." }));
            }
            var mergedSettings = new Dictionary<string, string>(current, StringComparer.Ordinal);
            foreach (var (key, value) in system.Settings) mergedSettings.TryAdd(key, value);
            merged.Add((system.Provider, mergedSettings));
        }

        // Dependencies: the manifest, each provider's own requirements and each test pack's requirements.
        var dependencies = new List<SetupDependency>(model.Dependencies);
        foreach (var system in model.Systems)
            dependencies.AddRange(await integrations.Handler(system.Provider).SetupRequirementsAsync(Destination(system, existing, request) ?? new ExternalIntegration { TargetEnvironmentId = environmentId, ProviderType = system.Provider }, system.Settings, ct));
        foreach (var pack in model.TestPacks) dependencies.AddRange(TestPackSetupRequirements.For(pack));
        // One requirement per (kind, reference); required wins over optional.
        dependencies = [.. dependencies.GroupBy(d => (d.Kind, Reference: d.Role is not null && d.Kind == SetupDependencyKind.TestData ? d.Role : d.Reference))
            .Select(group => group.OrderByDescending(d => d.Required).ThenByDescending(d => d.Reference != d.Role).First())];

        var resolutions = new List<SetupDependencyResolution>();
        foreach (var dependency in dependencies) resolutions.Add(await ResolveAsync(environmentId, dependency, model, existing, merged, ct));

        var block = new List<string>();
        if (issues.Any(issue => issue.Severity == SetupIssueSeverity.Error))
            block.Add($"{issues.Count(issue => issue.Severity == SetupIssueSeverity.Error)} package error(s): {issues.First(issue => issue.Severity == SetupIssueSeverity.Error).Message}");
        var unresolved = resolutions.Where(r => r.Dependency.Required && r.State is not SetupDependencyState.Resolved).ToList();
        if (unresolved.Count > 0) block.Add($"{unresolved.Count} required dependenc{(unresolved.Count == 1 ? "y" : "ies")} unresolved");
        return new SetupPreflightResult
        {
            Valid = issues.All(issue => issue.Severity != SetupIssueSeverity.Error), PackageName = model.Name, PackageVersion = model.Version, SchemaVersion = SetupPackageSchema.SupportedVersion,
            DetectedScope = model.Scope, DetectedProviders = [.. model.Systems.Select(system => system.Provider).Distinct()], Issues = issues, Changes = changes,
            Dependencies = resolutions, BlockReasons = block,
        };
    }

    private static ExternalIntegration? Destination(SetupPackageSystem system, IReadOnlyList<ExternalIntegration> existing, SetupPreflightRequest request) =>
        request.IntegrationId is { } id ? existing.FirstOrDefault(item => item.Id == id)
        : system.Provider == ExternalProviderType.Altinn ? existing.FirstOrDefault(item => item.ProviderType == ExternalProviderType.Altinn)
        : existing.FirstOrDefault(item => item.ProviderType == system.Provider && string.Equals(item.Name, system.Name, StringComparison.OrdinalIgnoreCase));

    private async Task<SetupDependencyResolution> ResolveAsync(string environmentId, SetupDependency d, SetupPackageModel model, IReadOnlyList<ExternalIntegration> existing,
        IReadOnlyList<(ExternalProviderType Provider, IReadOnlyDictionary<string, string> Settings)> merged, CancellationToken ct)
    {
        var missing = d.Required ? SetupDependencyState.Missing : SetupDependencyState.OptionalMissing;
        string externalHref = $"/admin/system-settings?section=target-environments&tab=external&profile={Uri.EscapeDataString(environmentId)}";
        foreach (var system in model.Systems.Select(s => s.Provider).Distinct())
            if (await integrations.Handler(system).ResolveAsync(environmentId, d, ct) is { } provided) return provided;

        switch (d.Kind)
        {
            case SetupDependencyKind.TestData:
            {
                var assets = testData.Assets();
                var byAlias = assets.Where(asset => string.Equals(asset.Alias, d.Reference, StringComparison.Ordinal)).ToList();
                var capability = d.Role?.ToLowerInvariant();
                List<TestDataAsset> candidates = byAlias.Count > 0 ? byAlias
                    : capability is null ? [] : [.. assets.Where(asset => asset.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))];
                const string owner = "Test Data", href = "/test-data";
                var label = d.Role is null ? d.Reference : $"{d.Role} ({d.Reference})";
                if (candidates.Count == 0) return new(d, missing, $"No Test Data asset for {label}. Nothing is generated.", owner, href);
                if (candidates.Count > 1) return new(d, SetupDependencyState.Ambiguous, $"{candidates.Count} Test Data assets match {label}; choose or mark one default.", owner, href);
                var asset = candidates[0];
                if (byAlias.Count > 0 && capability is not null && !asset.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                    return new(d, SetupDependencyState.Incompatible, $"{asset.Alias} is not a {d.Role} asset.", owner, href);
                if (asset.Approval != TestDataApproval.Approved) return new(d, SetupDependencyState.NotApproved, $"{asset.Alias} is not approved.", owner, href);
                return new(d, SetupDependencyState.Resolved, $"Resolved from Test Data: {asset.Alias} (identifier not shown).", owner, href);
            }
            case SetupDependencyKind.Integration:
            {
                IReadOnlyList<IntegrationDefinition> list;
                try { list = (await catalog.GetAsync(environmentId, null, null, ct)).Integrations; }
                catch (Exception ex) when (ex is not OperationCanceledException) { list = []; }
                const string owner = "Integration Quality Review", href = "/integration-quality-review";
                if (list.Any(item => item.Id == d.Reference)) return new(d, SetupDependencyState.Resolved, $"Resolved from IQR: {d.Reference}.", owner, href);
                var named = list.Where(item => string.Equals(item.DisplayName, d.Reference, StringComparison.OrdinalIgnoreCase)).ToList();
                return named.Count switch
                {
                    1 => new(d, SetupDependencyState.Resolved, $"Resolved from IQR by name: {named[0].Id}.", owner, href),
                    > 1 => new(d, SetupDependencyState.Ambiguous, $"{named.Count} integrations are named {d.Reference}; resolve in IQR.", owner, href),
                    _ => new(d, missing, $"Integration {d.Reference} is not configured in this environment. It is never created by import.", owner, href),
                };
            }
            case SetupDependencyKind.SourceSnapshot:
            {
                var snapshots = await sources.ListAsync(environmentId, ct);
                var matches = snapshots.Where(s => string.Equals(s.Archive.Sha256, d.Reference, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Archive.FileName, d.Reference, StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(s => s.Archive.Sha256).ToList();
                const string owner = "Source Analysis", href = "/source-analysis";
                return matches.Count switch
                {
                    1 => new(d, SetupDependencyState.Resolved, $"Resolved locally from Source Analysis: {matches[0].Archive.FileName}.", owner, href),
                    > 1 => new(d, SetupDependencyState.Ambiguous, $"{matches.Count} different snapshots match {d.Reference}; the newest is never chosen automatically.", owner, href),
                    _ => new(d, missing, $"No Source Analysis snapshot matches {d.Reference}.", owner, href),
                };
            }
            case SetupDependencyKind.ExternalIntegration:
                return existing.Any(item => string.Equals(item.Name, d.Reference, StringComparison.OrdinalIgnoreCase)) || model.Systems.Any(s => string.Equals(s.Name, d.Reference, StringComparison.OrdinalIgnoreCase))
                    ? new(d, SetupDependencyState.Resolved, "Configured in this environment or provided by this package.", "External Integrations", externalHref)
                    : new(d, missing, $"External integration {d.Reference} is not configured.", "External Integrations", externalHref);
            case SetupDependencyKind.ProviderCapability:
            {
                var offering = merged.Where(entry => ExternalProviderCatalog.Descriptor(entry.Provider).Capabilities.Contains(d.Reference, StringComparer.Ordinal)).ToList();
                var current = existing.Where(item => ExternalProviderCatalog.Descriptor(item.ProviderType).Capabilities.Contains(d.Reference, StringComparer.Ordinal)).ToList();
                foreach (var item in current) offering.Add((item.ProviderType, await integrations.Handler(item.ProviderType).CurrentSettingsAsync(item, ct)));
                if (offering.Count == 0) return new(d, missing, $"No external integration provides {d.Reference}.", "External Integrations", externalHref);
                return offering.Any(entry => integrations.Handler(entry.Provider).Satisfies(d.Reference, entry.Settings))
                    ? new(d, SetupDependencyState.Resolved, $"{d.Reference} is provided by a configured system.", "External Integrations", externalHref)
                    : new(d, SetupDependencyState.Incompatible, $"A {offering[0].Provider} system exists but does not satisfy {d.Reference} (required settings missing).", "External Integrations", externalHref);
            }
            case SetupDependencyKind.TestPack:
                return packs.Any(pack => string.Equals(pack.PackId, d.Reference, StringComparison.OrdinalIgnoreCase))
                    ? new(d, SetupDependencyState.Resolved, $"Test pack {d.Reference} is installed.", "Test packs", "/testing-capabilities")
                    : new(d, missing, $"Test pack {d.Reference} is not installed in this BirkNext.", "Test packs", "/testing-capabilities");
            default:
                return new(d, missing, $"No owner in this BirkNext can resolve {d.Kind} {d.Reference}.", "Integration Quality Review", "/integration-quality-review");
        }
    }

    /// <summary>
    /// Re-runs preflight and applies the whole plan in one unit of work, or nothing. Undecided conflicts, required unresolved dependencies and
    /// package errors refuse Apply. Trust, secrets, identities and runtime evidence are never part of the plan.
    /// </summary>
    public async Task<SetupApplyPackageResult> ApplyAsync(string environmentId, SetupApplyPackageRequest request, string provenance, CancellationToken ct)
    {
        var preflight = await PreflightAsync(environmentId, new SetupPreflightRequest(request.Content, request.FileName, request.Scope, request.Provider, request.IntegrationId), ct);
        if (!preflight.CanApply) return new(false, "Blocked: " + string.Join("; ", preflight.BlockReasons), [], preflight);
        var decisions = request.ConflictDecisions ?? [];
        var undecided = preflight.Changes.Where(change => change.Classification == "Conflict" && !decisions.ContainsKey(change.Id)).ToList();
        if (undecided.Count > 0) return new(false, $"Blocked: {undecided.Count} conflict(s) need a keep/apply decision.", [], preflight);

        var (model, _) = SetupPackageSchema.Parse(request.FileName, request.Content);
        var existing = await integrations.ListAsync(environmentId, ct);
        var applied = new List<string>();
        var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            foreach (var (system, index) in model!.Systems.Select((system, index) => (system, index)))
            {
                var destination = Destination(system, existing, new SetupPreflightRequest(request.Content, request.FileName, request.Scope, request.Provider, request.IntegrationId));
                var handler = integrations.Handler(system.Provider);
                var current = destination is null ? new Dictionary<string, string>() : await handler.CurrentSettingsAsync(destination, ct);
                var settings = new Dictionary<string, string>(current, StringComparer.Ordinal);
                foreach (var (key, value) in system.Settings)
                    if (!current.ContainsKey(key) || decisions.GetValueOrDefault($"{index}:{key}")) { settings[key] = value; if (current.GetValueOrDefault(key) != value) applied.Add($"{destination?.Name ?? system.Name}: {key}"); }
                var (saved, problems) = await integrations.SaveAsync(environmentId, destination?.Id, new ExternalIntegrationRequest(destination?.Name ?? system.Name, system.Provider,
                    system.EnvironmentName ?? destination?.EnvironmentName, destination?.Enabled ?? system.Enabled, settings), provenance, ct);
                if (saved is null) throw new SetupApplyException($"{system.Name}: {string.Join(" ", problems)}");
            }
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new(true, applied.Count == 0 ? "Nothing to change: the environment already matches the package." : $"Applied {applied.Count} value(s). {provenance}.", applied, preflight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            await integrations.ResyncAfterRollbackAsync(environmentId, CancellationToken.None);
            // A provider's own refusal is shown; any other failure is reported without its technical detail.
            var reason = ex is SetupApplyException ? ex.Message : "The setup could not be stored.";
            // Without a relational transaction (in-memory test host) earlier systems may be written: the operation is reported Failed, never applied.
            return new(false, $"Failed: {reason} No setup was applied{(transaction is null ? " (repair: re-run Apply after fixing the problem)" : "")}.", [], preflight);
        }
        finally { if (transaction is not null) await transaction.DisposeAsync(); }
    }

    private sealed class SetupApplyException(string message) : Exception(message);
}
