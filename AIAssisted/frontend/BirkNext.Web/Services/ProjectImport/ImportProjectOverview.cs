using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>One thing on Import Project that a tester should resolve (or know), with the action that resolves it.</summary>
public sealed record ImportAttentionItem(string Key, string Text, string? Detail, string ActionLabel, string Href, bool Informational = false);

/// <summary>One document type of the current project as Import Project summarizes it: what was found, what needs a choice, where to review it.</summary>
public sealed record ImportRoleSummary(WorkspaceArtifactType Role, string Title, string Found, string? Note, int RequiresClarification, string ActionLabel, string? Href);

/// <summary>One logical source of a testing setup in tester words ("MU source · 2 possible sources found · Choose source").</summary>
public sealed record ImportSetupLine(string Key, string Label, string State, bool NeedsAttention, bool Optional, string? ActionLabel, string? ActionName, string? Href);

/// <summary>
/// Import Project for testers: what was imported, what needs attention, and the testing setup in plain words. A thin projection over existing
/// state only — the current import record, the current workspace's project-wide pack overview (<see cref="ProjectPackOverview"/>), the
/// backend's source setup (<see cref="SourceSetupPresentation"/> rules) and the active Target Environment. It decides nothing new: ambiguity,
/// source matching and binding come from their owners.
/// </summary>
public static class ImportProjectOverview
{
    private static string S(int n) => n == 1 ? "" : "s";

    /// <summary>"14 modules · 28 feature packs · 167 documents · source code available": only counts that exist.</summary>
    public static string Summary(SddProjectImportRecord record, CurrentWorkspaceSnapshot? workspace, ProjectImportCurrentSource source)
    {
        var parts = new List<string>();
        if (workspace?.Overview is { } o)
        {
            if (o.HasModules) parts.Add($"{o.Modules} modules");
            if (o.PackCount > 0) parts.Add($"{o.PackCount} feature pack{S(o.PackCount)}");
        }
        parts.Add(record.ArtifactDocumentCount == 0 ? "no project documents" : $"{record.ArtifactDocumentCount} document{S(record.ArtifactDocumentCount)}");
        parts.Add(source.SourceDetected ? "source code available" : "no source code detected");
        return string.Join(" · ", parts);
    }

    /// <summary>What was imported, in one phrase: documents and source, documents only or source only. Each is a valid import.</summary>
    public static string ImportedKind(SddProjectImportRecord record, ProjectImportCurrentSource source) =>
        (record.ArtifactDocumentCount > 0, source.SourceDetected) switch
        {
            (true, true) => "Project documents and source code imported",
            (true, false) => "Project documents imported · no source code detected",
            (false, true) => "Source code imported · no supported project documents found",
            _ => "Nothing supported was found in this archive",
        };

    /// <summary>
    /// Source Analysis in plain words, with what "partial" means. The snapshot id and exact status stay in Technical details.
    /// </summary>
    public static (string Status, string? Explanation) SourceAnalysis(ProjectImportCurrentSource source, string? snapshotStatus)
    {
        if (source.SnapshotId is null)
            return source.SourceDetected
                ? ("Source code found · analysis not available", source.CanRetry ? "The source analysis could not be created. You can retry without uploading again." : source.Detail)
                : ("No source code in this project", null);
        return snapshotStatus switch
        {
            "Partial" => ("Source analysis available with limitations", "Some source-analysis categories could not be assessed (for example a technology BirkNext does not analyze)."),
            "Failed" => ("Source analysis available with limitations", "Source Analysis found no production project it supports, so most categories could not be assessed."),
            _ => ("Source analysis available", null),
        };
    }

    /// <summary>"Test environment: Development" or "Not selected" — the environment runtime and integration reviews use. Configured is not tested.</summary>
    public static string TestEnvironment(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName) ? "Test environment: Not selected" : $"Test environment: {environmentName}";

    // ── Project contents ─────────────────────────────────────────────────────────────

    /// <summary>"Specifications, Plans, Tasks, Data Models, Constitutions": every document type the project has.</summary>
    public static string DocumentTypes(ProjectPackOverview overview) =>
        string.Join(", ", overview.Roles.Where(r => r.Found).Select(r => r.Role switch
        {
            WorkspaceArtifactType.Tasks => "Tasks",
            WorkspaceArtifactType.Research => "Research",
            _ => Title(r.Role) + "s",
        }));

    public static string Title(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "Constitution",
        WorkspaceArtifactType.Specification => "Specification",
        WorkspaceArtifactType.Plan => "Plan",
        WorkspaceArtifactType.Tasks => "Tasks",
        WorkspaceArtifactType.DataModel => "Data Model",
        WorkspaceArtifactType.Research => "Research / supporting analysis",
        _ => ArtifactExplorerRoles.Label(role),
    };

    private static string Noun(WorkspaceArtifactType role, int count) => role switch
    {
        WorkspaceArtifactType.Constitution => count == 1 ? "constitution" : "constitutions",
        WorkspaceArtifactType.Specification => count == 1 ? "specification" : "specifications",
        WorkspaceArtifactType.Plan => count == 1 ? "plan" : "plans",
        WorkspaceArtifactType.Tasks => count == 1 ? "task set" : "task sets",
        WorkspaceArtifactType.DataModel => count == 1 ? "data model" : "data models",
        WorkspaceArtifactType.Research => count == 1 ? "supporting document" : "supporting documents",
        _ => "documents",
    };

    /// <summary>The tester's review action for a role ("Review specifications"); Research has no explorer.</summary>
    public static (string Label, string? Href) ReviewAction(WorkspaceArtifactType role) => role == WorkspaceArtifactType.Research
        ? ("Supporting documents are kept as context", null)
        : ($"Review {(role == WorkspaceArtifactType.Tasks ? "tasks" : Noun(role, 2))}", ProjectInputPresentation.ExplorerRoute(role));

    /// <summary>
    /// One summary per role that has documents: "40 specifications found across 13 modules", how many places require clarification (genuine
    /// same-pack ambiguity only — valid separate packs never count), and shared/base notes. From the project-wide pack overview.
    /// </summary>
    public static IReadOnlyList<ImportRoleSummary> Contents(ProjectPackOverview overview) =>
        [.. overview.Roles.Where(r => r.Found).Select(r =>
        {
            var found = $"{r.Documents} {Noun(r.Role, r.Documents)} found" + (overview.HasModules ? $" across {r.ModulesWithRole} module{S(r.ModulesWithRole)}" : "");
            var note = r.Role switch
            {
                WorkspaceArtifactType.Constitution when r.SharedDocuments > 0 => "Shared constitutions detected",
                WorkspaceArtifactType.DataModel when r.SharedDocuments > 0 && r.Documents > r.SharedDocuments => "Includes shared/base models",
                WorkspaceArtifactType.Research => "Context for reviewers; not reviewed on its own",
                _ => null,
            };
            var (label, href) = ReviewAction(r.Role);
            return new ImportRoleSummary(r.Role, Title(r.Role), found, note, r.Ambiguities.Count, label, href);
        })];

    public static string Clarification(int count) => count == 1 ? "1 requires clarification" : $"{count} require clarification";

    // ── Testing setup ────────────────────────────────────────────────────────────────

    /// <summary>A logical source in tester words. Technical terms (logical source, binding, IQR, Test Pack reference, IaC) stay in its details.</summary>
    public static ImportSetupLine Line(TestPackSourceStatus status)
    {
        var label = status.Role.Label;
        var optional = !SourceSetupPresentation.IsReference(status) && status.Role.Requirement != TestPackSourceRequirement.Required;
        var primary = SourceSetupPresentation.Primary(status, "#pi-choose-heading");
        var infrastructure = status.Role.RequiresInfrastructureAsCode;
        var (state, attention) = status.State switch
        {
            TestPackSourceState.ReferenceAvailable => ("Built-in test reference available", false),
            TestPackSourceState.ReferenceUnavailable => ("Built-in test reference could not be loaded", true),
            TestPackSourceState.ReviewRequired when SourceSetupPresentation.IsAmbiguous(status) =>
                ($"{status.Candidates.Select(c => c.Fingerprint).Distinct(StringComparer.Ordinal).Count()} possible sources found", true),
            TestPackSourceState.ReviewRequired => ("Source found · its integration binding needs review", true),
            TestPackSourceState.Bound or TestPackSourceState.Available or TestPackSourceState.AvailableNotBound when infrastructure => ("Infrastructure information available", false),
            TestPackSourceState.Bound or TestPackSourceState.Available or TestPackSourceState.AvailableNotBound => ("Found", false),
            TestPackSourceState.NotProvided => (infrastructure ? "Optional infrastructure information not provided" : "Optional · not provided", false),
            _ => ("Not found", true),
        };
        var action = attention || status.State is TestPackSourceState.NotProvided ? primary : null;
        return new ImportSetupLine(status.Role.Key, label, state, attention, optional, action?.Text, action?.AccessibleName, action?.Href);
    }

    /// <summary>"1 setup item requires attention" or "No setup item requires attention".</summary>
    public static string SetupHeadline(TestPackSourceSetup pack)
    {
        var n = pack.Sources.Select(Line).Count(l => l.NeedsAttention);
        return n == 0 ? "No setup item requires attention" : $"{n} setup item{S(n)} require{(n == 1 ? "s" : "")} attention";
    }

    // ── Needs attention ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Genuine, actionable conditions only, most important first: same-pack document ambiguity (never valid separate packs), a testing source
    /// with several possible matches or none, a source analysis that could not be created, and — as a note — no test environment selected.
    /// </summary>
    public static IReadOnlyList<ImportAttentionItem> Attention(CurrentWorkspaceSnapshot? workspace, SourceSetupView? setup, ProjectImportCurrentSource? source, bool environmentSelected)
    {
        var items = new List<ImportAttentionItem>();
        if (workspace?.Overview is { } o)
        {
            foreach (var role in o.Roles.Where(r => r.Ambiguities.Count > 0))
            {
                var n = role.Ambiguities.Count;
                var noun = Noun(role.Role, n);
                items.Add(new($"ambiguous-{role.Role}", $"{n} {Cap(noun)} require{(n == 1 ? "s" : "")} clarification",
                    $"Several possible files in one feature or folder — where: {string.Join("; ", role.Ambiguities.Select(a => a.Where))}.", "Choose file",
                    ProjectInputPresentation.ExplorerRoute(role.Role)));
            }
        }
        foreach (var pack in setup?.Packs ?? [])
            foreach (var status in pack.Sources)
            {
                var line = Line(status);
                if (!line.NeedsAttention || line.Optional) continue;
                items.Add(new($"setup-{pack.PackId}-{status.Role.Key}",
                    SourceSetupPresentation.IsAmbiguous(status) ? $"{status.Role.Label} source requires clarification" : $"{status.Role.Label}: {line.State}",
                    $"{pack.DisplayName} · {line.State}.", line.ActionLabel ?? "Review setup", line.Href ?? "#pi-setup-heading"));
            }
        if (source is { SnapshotId: null, SourceDetected: true })
            items.Add(new("source-analysis", "Source analysis is not available for this project", source.Detail, source.CanRetry ? "Retry" : "View details", "#pi-current-heading"));
        if (!environmentSelected)
            items.Add(new("test-environment", "Test environment not selected", "Integration and runtime reviews need one. Document review does not.",
                "Select test environment", NavigationCatalog.TargetEnvironmentsRoute, Informational: true));
        return items;
    }

    // ── Previously imported projects ─────────────────────────────────────────────────

    /// <summary>"Imported 24 Oct 2026".</summary>
    public static string Imported(DateTimeOffset at) => "Imported " + at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Source + 2 documents", "Source only", "2 documents" — what the earlier import holds.</summary>
    public static string Contents(PreviousImportRow row)
    {
        var documents = row.Documents.StartsWith("No ", StringComparison.Ordinal) ? null : row.Documents.Split(" · ")[0];
        return (row.HasSource, documents) switch
        {
            (true, { } d) => $"Source + {d}",
            (true, null) => "Source only",
            (false, { } d) => d,
            _ => "Nothing recorded",
        };
    }

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
