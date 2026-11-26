using System.Security.Cryptography;
using System.Text;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectEvidence;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>Where a new import goes: more evidence for the current application (default), or the first package of another application.</summary>
public enum ImportTarget { CurrentApplication, NewApplication }

/// <summary>What one package is for its application. From the package's content (Source Analysis project kinds, documents, IaC) — never its name.</summary>
public enum PackageEvidenceRole { PrimaryApplicationSource, RelatedApplicationSource, SupportingSource, Documentation, Infrastructure, GeneratedDocumentation, Contracts, NotClassified }

/// <summary>One package of an application with its role and its own (package-specific) Source Analysis status.</summary>
public sealed record ApplicationPackage(SddProjectImportRecord Record, ProjectEvidencePackage Package, PackageEvidenceRole Role, bool RoleConfirmed = false)
{
    private readonly IReadOnlyList<PackageEvidenceRole>? _roles;
    /// <summary>Every role of the package: all roles a person confirmed (several allowed), else its one detected role. <see cref="Role"/> is
    /// the primary one (for ordering and the one-line label).</summary>
    public IReadOnlyList<PackageEvidenceRole> Roles { get => _roles is { Count: > 0 } r ? r : [Role]; init => _roles = value; }

    public string ImportId => Record.ImportId;
    public string DisplayName => Package.DisplayName;
}

/// <summary>
/// Logical application vs imported package. A ZIP is evidence for one logical application (<see cref="SddApplicationContext"/>) — the last
/// imported ZIP is never the application by being last. Pure, deterministic rules:
/// <list type="bullet">
/// <item><see cref="Ensure"/> — a workspace saved before applications existed gets one application holding its imports (deterministic id; nothing
/// deleted, re-imported or re-bound).</item>
/// <item><see cref="Assign"/> — a new import joins the current application unless the tester chose "Import another application".</item>
/// <item><see cref="Packages"/> / <see cref="Primary"/> — the primary application source has a web/service host project and is the one the
/// application is named after (see <c>PrimaryOf</c>); a library-only package (M2LB.Common) is supporting/shared source whatever its name; another
/// package with its own host project (MU next to M2LB) is a related application/system source.</item>
/// <item><see cref="NameOf"/> — the application is named after its primary source; before one exists, after a shared package's family
/// ("M2LB.Common" → "M2LB"), else after its first package.</item>
/// <item><see cref="DocumentImport"/> — whose documents the workspace shows: the primary source's, so importing a supporting package never
/// replaces the application's documents. Import order does not change the result.</item>
/// </list>
/// </summary>
public static class ApplicationContexts
{
    public const string PrimarySourceBasis = "PrimarySource", SharedSourceBasis = "SharedSource", PackageBasis = "Package", ManualBasis = "Manual";

    /// <summary>Trailing name segments that mark a shared/supporting package of a larger application ("M2LB.Common").</summary>
    private static readonly HashSet<string> SharedSegments = new(StringComparer.OrdinalIgnoreCase)
        { "Common", "Shared", "Core", "Contracts", "Abstractions", "Lib", "Libs", "Library", "Libraries", "SharedKernel", "Kernel", "Utils", "Utilities" };

    /// <summary>Longest project name accepted (the Project list and headings stay readable).</summary>
    public const int MaxNameLength = 80;

    /// <summary>
    /// Why <paramref name="name"/> cannot be a project's name, or null when it can: not empty, at most <see cref="MaxNameLength"/> characters,
    /// and not the name of another project (two projects with one name cannot be told apart in the Project list).
    /// </summary>
    public static string? NameProblem(SddLifecycleState lifecycle, string? name, string? exceptApplicationId = null)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Enter a project name.";
        if (trimmed.Length > MaxNameLength) return $"Use at most {MaxNameLength} characters.";
        return lifecycle.Applications.Any(a => a.Id != exceptApplicationId && string.Equals(a.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)
                                             && Imports(lifecycle, a.Id).Count > 0)
            ? $"A project named “{trimmed}” already exists. Choose another name, or choose that project under Belongs to project."
            : null;
    }

    /// <summary>
    /// Renames a project. The name becomes the tester's own (Manual): later imports never rename it. Only the project's name changes — its
    /// packages keep their names (a package's name is its archive's root folder, which also identifies its versions). Returns the problem, or null.
    /// </summary>
    public static string? Rename(SddLifecycleState lifecycle, string applicationId, string? name)
    {
        if (Find(lifecycle, applicationId) is not { } application) return "The project no longer exists.";
        if (NameProblem(lifecycle, name, applicationId) is { } problem) return problem;
        application.Name = name!.Trim();
        application.NameBasis = ManualBasis;
        return null;
    }

    /// <summary>The name a new project gets from its first package unless the tester changes it ("M2LB.Common" → "M2LB", as <see cref="NameOf"/> does).</summary>
    public static string SuggestedName(string packageName) => FamilyOf(packageName) ?? packageName;

    public static SddApplicationContext? Find(SddLifecycleState lifecycle, string? applicationId) =>
        applicationId is null ? null : lifecycle.Applications.FirstOrDefault(a => a.Id == applicationId);

    /// <summary>The application the workspace works on, or null before any import.</summary>
    public static SddApplicationContext? Current(SddLifecycleState lifecycle) => Find(lifecycle, lifecycle.CurrentApplicationId);

    /// <summary>The packages (import records) bound to an application, oldest first.</summary>
    public static IReadOnlyList<SddProjectImportRecord> Imports(SddLifecycleState lifecycle, string? applicationId) =>
        applicationId is null ? [] : [.. lifecycle.ProjectImports.Where(i => i.ApplicationId == applicationId).OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal)];

    /// <summary>
    /// Path forward for a workspace saved before applications existed: its unassigned imports join the current application, or — when it has
    /// none — one application whose id is derived from its oldest import (stable across reloads before the next save). Nothing else changes.
    /// </summary>
    public static bool Ensure(SddLifecycleState lifecycle, DateTimeOffset now)
    {
        lifecycle.Applications ??= [];
        var changed = false;
        if (lifecycle.CurrentApplicationId is { } stale && Find(lifecycle, stale) is null) { lifecycle.CurrentApplicationId = null; changed = true; }
        var orphans = lifecycle.ProjectImports.Where(i => i.ApplicationId is null || Find(lifecycle, i.ApplicationId) is null)
            .OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal).ToList();
        if (orphans.Count > 0)
        {
            var target = Current(lifecycle) ?? lifecycle.Applications.FirstOrDefault();
            if (target is null)
            {
                target = new SddApplicationContext { Id = DerivedId(orphans[0].ImportId), CreatedAt = orphans[0].ImportedAt == default ? now : orphans[0].ImportedAt };
                lifecycle.Applications.Add(target);
            }
            foreach (var record in orphans) record.ApplicationId = target.Id;
            if (string.IsNullOrWhiteSpace(target.Name)) (target.Name, target.NameBasis) = NameOf(Imports(lifecycle, target.Id));
            changed = true;
        }
        // The application of the current import (a restored explicit choice). Without a current import nothing is chosen (V8, 2026-11-26): before,
        // the application with the newest import — or the first one — became current by itself.
        if (lifecycle.CurrentApplicationId is null && lifecycle.Applications.Count > 0
            && lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == lifecycle.CurrentProjectImportId)?.ApplicationId is { } ofCurrent)
        {
            lifecycle.CurrentApplicationId = ofCurrent;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Binds a (new or re-imported) package to an application and makes that application current. A re-import of a package already bound to
    /// an existing application stays with it unless another application was asked for. <paramref name="applicationId"/>: the project the tester
    /// chose in "Belongs to project" (any existing one, not only the current one); it wins over both. Returns the application.
    /// </summary>
    public static SddApplicationContext Assign(SddLifecycleState lifecycle, SddProjectImportRecord record, ImportTarget target, DateTimeOffset now, string? applicationId = null)
    {
        Ensure(lifecycle, now);
        SddApplicationContext application;
        if (target == ImportTarget.CurrentApplication && Find(lifecycle, applicationId) is { } chosen) application = chosen;
        else if (target == ImportTarget.CurrentApplication && Find(lifecycle, record.ApplicationId) is { } own) application = own;
        else if (target == ImportTarget.CurrentApplication && Current(lifecycle) is { } current) application = current;
        else
        {
            application = new SddApplicationContext { Id = "app-" + Guid.NewGuid().ToString("N"), CreatedAt = now };
            lifecycle.Applications.Add(application);
        }
        record.ApplicationId = application.Id;
        lifecycle.CurrentApplicationId = application.Id;
        if (application.NameBasis != ManualBasis)
        {
            var records = Imports(lifecycle, application.Id);
            (application.Name, application.NameBasis) = NameOf(records,
                application.NameBasis == PrimarySourceBasis ? records.FirstOrDefault(r => string.Equals(Display(r), application.Name, StringComparison.OrdinalIgnoreCase)) : null);
        }
        return application;
    }

    /// <summary>
    /// "Belongs to project" (2026-11-20): moves an imported package to another application of the workspace, or to a new one named
    /// <paramref name="newName"/> — for a package imported as "more evidence" of the wrong application (M2LB under Tiltak 2), so it becomes its
    /// own project without re-importing. Only the package's application changes: the import, its Source Analysis snapshot, documents, plans and
    /// every Target Environment's Project Evidence bindings (they reference the import id) are untouched. An application left without packages
    /// is removed; the other application's derived name is recomputed. Returns the target application, or the problem.
    /// </summary>
    public static (SddApplicationContext? Application, string? Problem) MoveImport(SddLifecycleState lifecycle, string importId, string? targetApplicationId, string? newName, DateTimeOffset now)
    {
        Ensure(lifecycle, now);
        if (lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId) is not { } record) return (null, "This package is no longer imported.");
        var from = Find(lifecycle, record.ApplicationId);
        SddApplicationContext target;
        if (!string.IsNullOrWhiteSpace(targetApplicationId))
        {
            if (Find(lifecycle, targetApplicationId) is not { } existing) return (null, "That project no longer exists.");
            if (existing.Id == from?.Id) return (null, "The package already belongs to that project.");
            target = existing;
        }
        else
        {
            var name = newName?.Trim() ?? "";
            if (name.Length is 0 or > 120) return (null, "Give the new project a name (at most 120 characters).");
            if (lifecycle.Applications.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"A project named {name} already exists: choose it instead of creating another.");
            target = new SddApplicationContext { Id = "app-" + Guid.NewGuid().ToString("N"), Name = name, NameBasis = ManualBasis, CreatedAt = now };
            lifecycle.Applications.Add(target);
        }
        record.ApplicationId = target.Id;
        record.KeptWithApplication = false;
        // The package whose documents the workspace shows stays shown: its new project becomes the current one (never a project without it).
        if (lifecycle.CurrentProjectImportId == importId) lifecycle.CurrentApplicationId = target.Id;
        if (from is not null)
        {
            var remaining = Imports(lifecycle, from.Id);
            if (remaining.Count == 0)
            {
                lifecycle.Applications.Remove(from);
                if (lifecycle.CurrentApplicationId == from.Id) lifecycle.CurrentApplicationId = target.Id;
            }
            else if (from.NameBasis != ManualBasis) (from.Name, from.NameBasis) = NameOf(remaining);
        }
        if (target.NameBasis != ManualBasis) (target.Name, target.NameBasis) = NameOf(Imports(lifecycle, target.Id));
        return (target, null);
    }

    /// <summary>
    /// Whether <paramref name="package"/> looks like a different application than the project it was imported into (M2LB added as "more evidence"
    /// of Tiltak 2): it has its own startup (web/service host) project, the project has other packages, and its name shares nothing with them or
    /// with a name the tester gave the project. A package that is not the primary source of a project that already has one is the "related
    /// system" case (MU next to M2LB) and is flagged too. Never flagged once the tester confirmed its roles or chose to keep it.
    /// </summary>
    public static bool LooksLikeAnotherApplication(ApplicationPackage package, IReadOnlyList<ApplicationPackage> packages, SddApplicationContext? application)
    {
        if (package.RoleConfirmed || package.Record.KeptWithApplication || !package.Package.Has(ProjectEvidenceCapability.ApplicationSource)) return false;
        var others = packages.Where(p => p.ImportId != package.ImportId).ToList();
        if (others.Count == 0) return false;
        if (package.Role == PackageEvidenceRole.PrimaryApplicationSource && others.Any(o => o.Package.Has(ProjectEvidenceCapability.ApplicationSource))) return false;
        var key = NameKey(package.DisplayName);
        if (key.Length == 0 || others.Any(o => NameKey(o.DisplayName) == key || NameKey(FamilyOf(o.DisplayName) ?? "") == key)) return false;
        return !(application is { NameBasis: ManualBasis } manual && NameKey(manual.Name) == key);
    }

    /// <summary>The first word of a name, lower case ("M2LB.Common" → "m2lb", "Tiltak 2 - Sikkert …" → "tiltak").</summary>
    public static string NameKey(string? name) =>
        (name ?? "").Split(['.', ' ', '-', '_', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";

    /// <summary>The role of a package, from its capabilities. Null-safe: without source setup a package's source is not classified.</summary>
    public static PackageEvidenceRole RoleOf(ProjectEvidencePackage package) =>
        package.Has(ProjectEvidenceCapability.ApplicationSource) ? PackageEvidenceRole.PrimaryApplicationSource
        : package.Has(ProjectEvidenceCapability.SupportingSource) ? PackageEvidenceRole.SupportingSource
        : package.Has(ProjectEvidenceCapability.Infrastructure) ? PackageEvidenceRole.Infrastructure
        : package.Has(ProjectEvidenceCapability.Documentation) ? PackageEvidenceRole.Documentation
        : package.Has(ProjectEvidenceCapability.GeneratedDocumentation) ? PackageEvidenceRole.GeneratedDocumentation
        : package.Has(ProjectEvidenceCapability.ContractSchema) ? PackageEvidenceRole.Contracts
        : PackageEvidenceRole.NotClassified;

    public static string RoleLabel(PackageEvidenceRole role) => role switch
    {
        PackageEvidenceRole.PrimaryApplicationSource => "Primary application source",
        PackageEvidenceRole.RelatedApplicationSource => "Related application/system source",
        PackageEvidenceRole.SupportingSource => "Supporting/shared source",
        PackageEvidenceRole.Documentation => "Documentation",
        PackageEvidenceRole.Infrastructure => "Infrastructure evidence",
        PackageEvidenceRole.GeneratedDocumentation => "Generated documentation",
        PackageEvidenceRole.Contracts => "Contracts/schemas",
        _ => "Not classified yet",
    };

    /// <summary>
    /// The application's packages with their roles. Roles need the backend's source setup (Source Analysis project kinds): without it a
    /// package with source is "Not classified yet", never guessed to be the application source.
    /// </summary>
    public static IReadOnlyList<ApplicationPackage> Packages(SddLifecycleState lifecycle, string? applicationId, SourceSetupView? setup)
    {
        var packages = Imports(lifecycle, applicationId).Select(record =>
        {
            var package = ProjectEvidencePackages.Of(record, lifecycle, setup);
            // A role the person confirmed in Source Management is the package's canonical role; without one the role detected from content.
            if (ConfirmedRole(record) is { } confirmed) return new ApplicationPackage(record, package, confirmed, RoleConfirmed: true) { Roles = ConfirmedRoles(record) };
            var role = Classified(record, setup) ? RoleOf(package) : PackageEvidenceRole.NotClassified;
            return new ApplicationPackage(record, package, role);
        }).ToList();
        // One primary application source; any other package with its own host project is a related application/system (MU next to M2LB). A
        // confirmed primary is never demoted, and when one exists it is the primary.
        var primary = packages.FirstOrDefault(p => p.RoleConfirmed && p.Role == PackageEvidenceRole.PrimaryApplicationSource) ?? PrimaryOf(packages, Find(lifecycle, applicationId));
        return [.. packages.Select(p => p.Role == PackageEvidenceRole.PrimaryApplicationSource && p != primary && !p.RoleConfirmed ? p with { Role = PackageEvidenceRole.RelatedApplicationSource } : p)];
    }

    /// <summary>
    /// The canonical role a person confirmed for the package in Source Management (several may be confirmed; the association role is the
    /// first in <see cref="PackageEvidenceRole"/> order, so an application source wins over its documentation). Null when none is confirmed.
    /// Capabilities stay what the content shows: a confirmed role never adds a capability.
    /// </summary>
    public static PackageEvidenceRole? ConfirmedRole(SddProjectImportRecord record) => ConfirmedRoles(record).Cast<PackageEvidenceRole?>().FirstOrDefault();

    /// <summary>
    /// Every role a person confirmed for the package, in <see cref="PackageEvidenceRole"/> order (unknown names are skipped, never guessed).
    /// Several roles are independent facts: a package can be an integration source and a contracts source at once (2026-11-19: before, only the
    /// first one was used and the others were silently ignored downstream).
    /// </summary>
    public static IReadOnlyList<PackageEvidenceRole> ConfirmedRoles(SddProjectImportRecord record) =>
        [.. record.ConfirmedRoles.Select(r => Enum.TryParse<PackageEvidenceRole>(r, out var role) ? role : (PackageEvidenceRole?)null).OfType<PackageEvidenceRole>()
            .Where(r => r != PackageEvidenceRole.NotClassified).Distinct().Order()];

    /// <summary>
    /// Whether the backend has classified the package: a package without source is classified by its documents alone; a package with a source
    /// snapshot only once the source setup reports that snapshot (its project kinds). Otherwise its role is not guessed.
    /// </summary>
    private static bool Classified(SddProjectImportRecord record, SourceSetupView? setup) =>
        record.SourceSnapshotId is null && record.SourceState is not ("Created" or "Reused")
        || setup is not null && ProjectImportHistory.SnapshotsOf(record, setup.Snapshots).Count > 0;

    /// <summary>The primary application source of <see cref="Packages"/> (null when none).</summary>
    public static ApplicationPackage? Primary(IReadOnlyList<ApplicationPackage> packages) =>
        packages.FirstOrDefault(p => p.Role == PackageEvidenceRole.PrimaryApplicationSource);

    /// <summary>
    /// Among packages with a web/service host project: the one the application is named after — its manual name, or the application a shared
    /// package belongs to (M2LB.Common → M2LB) — the newest import of that name first (a re-imported version); else the earliest such package.
    /// Independent of import order whenever a shared package or a manual name identifies the application.
    /// </summary>
    private static ApplicationPackage? PrimaryOf(IReadOnlyList<ApplicationPackage> packages, SddApplicationContext? application)
    {
        var sources = packages.Where(p => p.Role == PackageEvidenceRole.PrimaryApplicationSource).ToList();
        if (sources.Count == 0) return null;
        var anchors = packages.Select(p => FamilyOf(p.DisplayName)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (application is { NameBasis: ManualBasis, Name.Length: > 0 } manual) anchors.Add(manual.Name);
        return sources.Where(p => anchors.Contains(p.DisplayName)).OrderByDescending(p => p.Record.ImportedAt).ThenBy(p => p.ImportId, StringComparer.Ordinal).FirstOrDefault()
            ?? sources.OrderBy(p => p.Record.ImportedAt).ThenBy(p => p.ImportId, StringComparer.Ordinal).First();
    }

    /// <summary>
    /// (Name, basis) of an application from its import records (oldest first) and, when classified, its primary source: the primary source's
    /// name; else the application a shared package is named after; else its first package's name.
    /// </summary>
    public static (string Name, string Basis) NameOf(IReadOnlyList<SddProjectImportRecord> records, SddProjectImportRecord? primary = null)
    {
        if (primary is not null) return (Display(primary), PrimarySourceBasis);
        foreach (var record in records)
            if (FamilyOf(Display(record)) is { } family) return (family, SharedSourceBasis);
        return records.Count == 0 ? ("Unnamed application", PackageBasis) : (Display(records[0]), PackageBasis);
    }

    private static string Display(SddProjectImportRecord r) => string.IsNullOrWhiteSpace(r.ProjectName) ? StripZip(r.ArchiveFileName) : r.ProjectName.Trim();

    /// <summary>"M2LB.Common" → "M2LB": the application a shared package is named after; null when the name does not mark a shared package.</summary>
    public static string? FamilyOf(string name)
    {
        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 && SharedSegments.Contains(segments[^1]) ? string.Join('.', segments[..^1]) : null;
    }

    /// <summary>Refreshes each derived application name from classified packages (a manual name is never changed). True when a name changed.</summary>
    public static bool Reconcile(SddLifecycleState lifecycle, SourceSetupView? setup)
    {
        if (setup is null) return false;
        var changed = false;
        foreach (var application in lifecycle.Applications.Where(a => a.NameBasis != ManualBasis))
        {
            var packages = Packages(lifecycle, application.Id, setup);
            var (name, basis) = NameOf([.. packages.Select(p => p.Record)], Primary(packages)?.Record);
            if (name != application.Name || basis != application.NameBasis) { application.Name = name; application.NameBasis = basis; changed = true; }
        }
        return changed;
    }

    /// <summary>
    /// Whose documents the workspace shows for an application after an import (<paramref name="fallbackImportId"/> = the package just shown):
    /// the primary application source's when one exists; else the documentation package with the most documents. A package the backend has not
    /// classified yet is never moved away from (its role is unknown, nothing is guessed), and the primary source keeps its place.
    /// </summary>
    public static string? DocumentImport(SddLifecycleState lifecycle, string applicationId, SourceSetupView? setup, string? fallbackImportId)
    {
        var packages = Packages(lifecycle, applicationId, setup);
        if (packages.FirstOrDefault(p => p.ImportId == fallbackImportId) is { Role: PackageEvidenceRole.NotClassified or PackageEvidenceRole.PrimaryApplicationSource } kept)
            return kept.ImportId;
        if (Primary(packages) is { } primary) return primary.ImportId;
        var documented = packages.Where(p => p.Package.DocumentCount > 0).OrderByDescending(p => p.Package.DocumentCount).ThenByDescending(p => p.Record.ImportedAt).FirstOrDefault();
        return documented?.ImportId ?? fallbackImportId;
    }

    /// <summary>"app-" + the first 16 hex of SHA-256 of the oldest import id: the same legacy workspace always gets the same application id.</summary>
    private static string DerivedId(string importId) =>
        "app-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(importId)))[..16].ToLowerInvariant();

    private static string StripZip(string fileName) => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
}
