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
        if (lifecycle.CurrentApplicationId is null && lifecycle.Applications.Count > 0)
        {
            // The application of the current import, else the one with the newest import.
            lifecycle.CurrentApplicationId = lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == lifecycle.CurrentProjectImportId)?.ApplicationId
                ?? lifecycle.ProjectImports.OrderByDescending(i => i.ImportedAt).FirstOrDefault()?.ApplicationId ?? lifecycle.Applications[0].Id;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Binds a (new or re-imported) package to an application and makes that application current. A re-import of a package already bound to
    /// an existing application stays with it unless another application was asked for. Returns the application.
    /// </summary>
    public static SddApplicationContext Assign(SddLifecycleState lifecycle, SddProjectImportRecord record, ImportTarget target, DateTimeOffset now)
    {
        Ensure(lifecycle, now);
        SddApplicationContext application;
        if (target == ImportTarget.CurrentApplication && Find(lifecycle, record.ApplicationId) is { } own) application = own;
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
            if (ConfirmedRole(record) is { } confirmed) return new ApplicationPackage(record, package, confirmed, RoleConfirmed: true);
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
    public static PackageEvidenceRole? ConfirmedRole(SddProjectImportRecord record) =>
        record.ConfirmedRoles.Select(r => Enum.TryParse<PackageEvidenceRole>(r, out var role) ? role : (PackageEvidenceRole?)null).OfType<PackageEvidenceRole>()
            .Where(r => r != PackageEvidenceRole.NotClassified).Order().Cast<PackageEvidenceRole?>().FirstOrDefault();

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
