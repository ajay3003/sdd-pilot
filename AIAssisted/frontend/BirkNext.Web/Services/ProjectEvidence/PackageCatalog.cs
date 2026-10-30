using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>A package role: what a recommended package is for (Main Application, Shared Source …). Not an evidence capability and not readiness.</summary>
public sealed class PackageRoleDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Predefined roles are protected: they cannot be renamed or removed.</summary>
    [JsonIgnore] public bool Predefined { get; init; }
}

/// <summary>One recommended package: what to import (a friendly name, the expected file name or a pattern), its role and whether it is required.
/// Editing it never changes an imported ZIP, a binding or readiness.</summary>
public sealed class PackageRecommendation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    /// <summary>File name or pattern ("M2LB.zip", "M2LB*.zip"); <c>*</c> and <c>?</c> are wildcards, matching is case-insensitive.</summary>
    [JsonPropertyName("filePattern")] public string FilePattern { get; set; } = "";
    [JsonPropertyName("roleId")] public string RoleId { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
    [JsonPropertyName("order")] public int Order { get; set; }
}

/// <summary>The project's package catalog: recommendations and the project's custom package roles (predefined roles are not stored).</summary>
public sealed class PackageCatalog
{
    [JsonPropertyName("roles")] public List<PackageRoleDefinition> CustomRoles { get; set; } = [];
    [JsonPropertyName("packages")] public List<PackageRecommendation> Packages { get; set; } = [];
}

public enum RecommendationStatus { Imported, Missing, Ambiguous }

/// <summary>A recommendation with its import status, matched against the authoritative import registry (never a separate list).</summary>
public sealed record RecommendationMatch(PackageRecommendation Recommendation, string RoleName, RecommendationStatus Status, IReadOnlyList<SddProjectImportRecord> Imports)
{
    public string StatusLabel => Status switch
    {
        RecommendationStatus.Imported => "Imported",
        RecommendationStatus.Ambiguous => $"{Imports.Count} imports match — review",
        _ => Recommendation.Required ? "Missing" : "Not imported (optional)",
    };
}

/// <summary>
/// The one project-scoped package catalog behind Recommended Workflow (simplified view) and Target Environment → Project (configuration). Keyed
/// by the logical project (the Target Application name), so every environment of that project shares it; evidence bindings stay per Target
/// Environment. Stored with the Target Environment settings. Imports are matched from the workspace's Project Import records — the same registry
/// Import Project and Project Evidence use. A recommendation never creates an evidence binding and never makes anything Ready.
/// </summary>
public sealed class PackageCatalogService(IFrontendAnalysisSettingsService settings, IWorkspaceSessionService workspace, IJSRuntime js)
{
    public static readonly IReadOnlyList<PackageRoleDefinition> PredefinedRoles =
    [
        new() { Id = "main-application", Name = "Main Application", Predefined = true },
        new() { Id = "shared-source", Name = "Shared Source", Predefined = true },
        new() { Id = "documentation", Name = "Documentation", Predefined = true },
        new() { Id = "infrastructure", Name = "Infrastructure / Terraform", Predefined = true },
        new() { Id = "api-contracts", Name = "API Contracts / Schemas", Predefined = true },
        new() { Id = "integration-adapter", Name = "Integration / Adapter", Predefined = true },
        new() { Id = "testing", Name = "Testing / Test Data", Predefined = true },
        new() { Id = "generated-docs", Name = "Generated Documentation", Predefined = true },
    ];

    public event Action? Changed;

    private static string Key(string project) => project.Trim().ToUpperInvariant();

    private PackageCatalog? Find(string project) =>
        settings.Settings.PackageCatalogs.FirstOrDefault(c => Key(c.Key) == Key(project)).Value;

    /// <summary>The catalog of a project (empty when none is defined yet; nothing is created by reading).</summary>
    public PackageCatalog Catalog(string project) => Find(project) ?? new PackageCatalog();

    public IReadOnlyList<PackageRoleDefinition> Roles(string project) => [.. PredefinedRoles, .. Catalog(project).CustomRoles];

    public string RoleName(string project, string roleId) => Roles(project).FirstOrDefault(r => r.Id == roleId)?.Name ?? "Unknown role";

    /// <summary>Recommendations in their order, each matched against the imported packages.</summary>
    public IReadOnlyList<RecommendationMatch> Matches(string project)
    {
        var imports = workspace.SddLifecycle.ProjectImports;
        return [.. Catalog(project).Packages.OrderBy(p => p.Order).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var matching = imports.Where(i => Matches(p.FilePattern, i.ArchiveFileName)).ToList();
            // Several imports of the same archive content are one package imported again, not an ambiguity.
            var distinct = matching.Select(i => i.ArchiveSha256).Distinct(StringComparer.Ordinal).Count();
            return new RecommendationMatch(p, RoleName(project, p.RoleId),
                matching.Count == 0 ? RecommendationStatus.Missing : distinct > 1 && !HasWildcard(p.FilePattern) ? RecommendationStatus.Ambiguous : RecommendationStatus.Imported,
                [.. matching.OrderByDescending(i => i.ImportedAt)]);
        })];
    }

    private static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    /// <summary>Case-insensitive file-name match; <c>*</c> and <c>?</c> are wildcards. A pattern without ".zip" also matches "&lt;pattern&gt;.zip".</summary>
    public static bool Matches(string pattern, string fileName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(fileName)) return false;
        var p = pattern.Trim();
        var regex = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + (p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || p.EndsWith('*') ? "" : "(\\.zip)?") + "$";
        return Regex.IsMatch(fileName.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private async Task<string?> MutateAsync(string project, Func<PackageCatalog, string?> change)
    {
        if (string.IsNullOrWhiteSpace(project)) return "Choose the project (Target Application) first.";
        var catalog = Find(project);
        var created = catalog is null;
        catalog ??= new PackageCatalog();
        if (change(catalog) is { } error) return error;
        if (created) settings.Settings.PackageCatalogs[project.Trim()] = catalog;
        if (settings.ActiveProfile?.Id is { } id) await settings.MaterializeAsync(js, id);
        await settings.SaveAsync(js);
        Changed?.Invoke();
        return null;
    }

    private static string Slug(string text) => new string([.. text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');

    /// <summary>Adds a custom role. Names are unique (predefined included, case-insensitive). Returns the error, or null.</summary>
    public Task<string?> AddRoleAsync(string project, string name) => MutateAsync(project, catalog =>
    {
        var text = name.Trim();
        if (text.Length is 0 or > 60) return "Enter a role name (at most 60 characters).";
        if (Roles(project).Any(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))) return $"A role named “{text}” already exists.";
        var id = "custom-" + (Slug(text) is { Length: > 0 } s ? s : "role");
        for (var n = 2; Roles(project).Any(r => r.Id == id); n++) id = $"custom-{Slug(text)}-{n}";
        catalog.CustomRoles.Add(new PackageRoleDefinition { Id = id, Name = text });
        return null;
    });

    /// <summary>Renames a custom role (recommendations keep it: they reference the id). Predefined roles are protected.</summary>
    public Task<string?> RenameRoleAsync(string project, string roleId, string name) => MutateAsync(project, catalog =>
    {
        var role = catalog.CustomRoles.FirstOrDefault(r => r.Id == roleId);
        if (role is null) return PredefinedRoles.Any(r => r.Id == roleId) ? "Predefined roles cannot be renamed." : "The role does not exist.";
        var text = name.Trim();
        if (text.Length is 0 or > 60) return "Enter a role name (at most 60 characters).";
        if (Roles(project).Any(r => r.Id != roleId && string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))) return $"A role named “{text}” already exists.";
        role.Name = text;
        return null;
    });

    /// <summary>Removes a custom role. A role still used by recommendations is removed only with an explicit <paramref name="reassignTo"/>.</summary>
    public Task<string?> RemoveRoleAsync(string project, string roleId, string? reassignTo = null) => MutateAsync(project, catalog =>
    {
        if (PredefinedRoles.Any(r => r.Id == roleId)) return "Predefined roles cannot be removed.";
        if (catalog.CustomRoles.FirstOrDefault(r => r.Id == roleId) is not { } role) return "The role does not exist.";
        var users = catalog.Packages.Where(p => p.RoleId == roleId).ToList();
        if (users.Count > 0)
        {
            if (reassignTo is null) return $"“{role.Name}” is used by {users.Count} recommended package{(users.Count == 1 ? "" : "s")}. Choose another role for {(users.Count == 1 ? "it" : "them")} first.";
            if (reassignTo == roleId || Roles(project).All(r => r.Id != reassignTo)) return "Choose an existing role to reassign to.";
            foreach (var p in users) p.RoleId = reassignTo;
        }
        catalog.CustomRoles.Remove(role);
        return null;
    });

    /// <summary>Adds or updates a recommendation. A role given by a new name is created as a custom role (the combo box "add" path).</summary>
    public Task<string?> SaveRecommendationAsync(string project, string? id, string displayName, string filePattern, string roleNameOrId, bool required) =>
        MutateAsync(project, catalog =>
        {
            var name = displayName.Trim();
            var pattern = filePattern.Trim();
            if (name.Length is 0 or > 120) return "Enter a display name (at most 120 characters).";
            if (pattern.Length is 0 or > 200 || pattern.IndexOfAny(['/', '\\']) >= 0) return "Enter the expected file name or pattern (e.g. M2LB.zip or M2LB*.zip), without a folder.";
            var roles = Roles(project);
            var role = roles.FirstOrDefault(r => r.Id == roleNameOrId) ?? roles.FirstOrDefault(r => string.Equals(r.Name, roleNameOrId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                var roleName = roleNameOrId.Trim();
                if (roleName.Length is 0 or > 60) return "Choose a package role or type a new one.";
                role = new PackageRoleDefinition { Id = "custom-" + (Slug(roleName) is { Length: > 0 } s ? s : "role"), Name = roleName };
                for (var n = 2; roles.Any(r => r.Id == role.Id); n++) role.Id = $"custom-{Slug(roleName)}-{n}";
                catalog.CustomRoles.Add(role);
            }
            if (catalog.Packages.Any(p => p.Id != id && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase))) return $"A recommended package named “{name}” already exists.";
            var recommendation = id is null ? null : catalog.Packages.FirstOrDefault(p => p.Id == id);
            if (id is not null && recommendation is null) return "The recommended package does not exist.";
            if (recommendation is null)
            {
                recommendation = new PackageRecommendation { Id = Guid.NewGuid().ToString("N"), Order = catalog.Packages.Count == 0 ? 0 : catalog.Packages.Max(p => p.Order) + 1 };
                catalog.Packages.Add(recommendation);
            }
            recommendation.DisplayName = name;
            recommendation.FilePattern = pattern;
            recommendation.RoleId = role.Id;
            recommendation.Required = required;
            return null;
        });

    public Task<string?> RemoveRecommendationAsync(string project, string id) => MutateAsync(project, catalog =>
        catalog.Packages.RemoveAll(p => p.Id == id) > 0 ? null : "The recommended package does not exist.");

    /// <summary>Moves a recommendation up (-1) or down (+1) in the order.</summary>
    public Task<string?> MoveAsync(string project, string id, int delta) => MutateAsync(project, catalog =>
    {
        var ordered = catalog.Packages.OrderBy(p => p.Order).ToList();
        var index = ordered.FindIndex(p => p.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count) return "It cannot move further.";
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return null;
    });

    /// <summary>The logical project of an environment: its Target Application name (else its Project evidence name).</summary>
    public string? ProjectOf(FrontendAnalysisProfile? profile) => TargetApplications.Of(profile)?.Name ?? profile?.Project?.ProjectName;
}
