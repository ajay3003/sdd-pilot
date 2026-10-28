using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>Who resolves an attention item: the project's documents, a testing setup, the source analysis or the test environment.</summary>
public enum ImportAttentionGroup { Documents, TestingSetup, SourceAnalysis, Environment }

/// <summary>One thing on Import Project that a tester should resolve (or know), with the action that resolves it.</summary>
public sealed record ImportAttentionItem(string Key, string Text, string? Detail, string ActionLabel, string Href, bool Informational = false,
    ImportAttentionGroup Group = ImportAttentionGroup.Documents);

/// <summary>Source Analysis of one import, separate from the import itself (an import succeeds even when no source analysis exists).</summary>
public enum ImportAnalysisState { NoSource, NotCreated, Available, Ready, Partial, NoApplicationCode, Failed }

/// <summary>
/// Source Analysis as Import Project shows it: a short status, why, and which areas hold evidence. <see cref="HasDetails"/> marks a state whose
/// details live in Source Analysis (Partial / no application code / Failed).
/// </summary>
public sealed record ImportSourceAnalysis(ImportAnalysisState State, string Status, ProjectImportTone Tone, string? Explanation, string? Areas)
{
    public bool HasDetails => State is ImportAnalysisState.Partial or ImportAnalysisState.NoApplicationCode or ImportAnalysisState.Failed;
}

/// <summary>The source role a testing setup recognized in a snapshot ("MU (Meldingsutveksleren)"), or why none was.</summary>
public sealed record ImportSourceRole(string Status, string? Detail, bool Identified);

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

    /// <summary>The current project's Source Analysis (see <see cref="SourceAnalysis(bool, Guid?, SourceSetupSnapshot?, string?, bool)"/>).</summary>
    public static ImportSourceAnalysis SourceAnalysis(ProjectImportCurrentSource source, SourceSetupSnapshot? snapshot) =>
        SourceAnalysis(source.SourceDetected, source.SnapshotId, snapshot, source.Detail, source.CanRetry);

    /// <summary>
    /// Source Analysis in plain words, never the import's outcome. The snapshot's own Failed means only that the application-code analyzer found no
    /// production project: architecture and the evidence domains are analyzed independently, so a Failed snapshot that still holds infrastructure,
    /// configuration, CI/CD or contract evidence is shown as partial evidence with no application code — and Failed only when no area holds any.
    /// </summary>
    public static ImportSourceAnalysis SourceAnalysis(bool sourceDetected, Guid? snapshotId, SourceSetupSnapshot? snapshot, string? notCreatedDetail = null, bool canRetry = false)
    {
        if (snapshotId is null)
            return sourceDetected
                ? new(ImportAnalysisState.NotCreated, "Not created", ProjectImportTone.Partial,
                    canRetry ? "The import succeeded, but no source analysis was created. You can retry without uploading again." : notCreatedDetail, null)
                : new(ImportAnalysisState.NoSource, "No source code", ProjectImportTone.Neutral, null, null);
        var areas = snapshot?.Areas ?? [];
        var summary = AreaSummary(areas);
        return snapshot?.Status switch
        {
            null => new(ImportAnalysisState.Available, "Available", ProjectImportTone.Available, null, null),
            "Ready" => new(ImportAnalysisState.Ready, "Ready", ProjectImportTone.Available, null, summary),
            "Partial" => new(ImportAnalysisState.Partial, "Partial", ProjectImportTone.Partial, "Some areas could not be fully analyzed.", summary),
            _ when areas.Count == 0 => new(ImportAnalysisState.NoApplicationCode, "No production project found", ProjectImportTone.Partial,
                "Code analysis found no supported production project. Source Analysis shows which other areas hold evidence.", null),
            _ when areas.Any(Usable) => new(ImportAnalysisState.NoApplicationCode, "Partial · no application code", ProjectImportTone.Partial,
                "No supported production project was found, so code-level analysis did not run. The other source evidence is available.", summary),
            _ => new(ImportAnalysisState.Failed, "Failed", ProjectImportTone.Rejected,
                "Nothing in this source could be analyzed: no supported production project and no architecture, infrastructure, configuration, CI/CD or contract evidence.", summary),
        };
    }

    private static bool Usable(SourceSetupArea area) => area.Status is "Complete" or "Partial";

    /// <summary>"Infrastructure as Code, Configuration complete · CI/CD partial · Architecture not supported · no supported application code".
    /// Areas not detected in the source are omitted (absence says nothing about the project).</summary>
    public static string? AreaSummary(IReadOnlyList<SourceSetupArea> areas)
    {
        if (areas.Count == 0) return null;
        string Names(string status) => string.Join(", ", areas.Where(a => a.Status == status && a.Area != "Application code").Select(a => a.Area));
        var parts = new List<string>();
        if (Names("Complete") is { Length: > 0 } complete) parts.Add($"{complete} complete");
        if (Names("Partial") is { Length: > 0 } partial) parts.Add($"{partial} partial");
        if (Names("Failed") is { Length: > 0 } failed) parts.Add($"{failed} failed");
        if (Names("Unsupported") is { Length: > 0 } unsupported) parts.Add($"{unsupported} not supported");
        switch (areas.FirstOrDefault(a => a.Area == "Application code")?.Status)
        {
            case "Complete": parts.Insert(0, "Application code complete"); break;
            case "Partial": parts.Insert(0, "Application code partial"); break;
            case "Failed": parts.Add("application code could not be read"); break;
            case "NotDetected": parts.Add("no supported application code"); break;
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// The source role(s) the testing setups recognized in a snapshot — from its contents (components, projects, API documents, declared
    /// infrastructure), never its archive name — or "Not identified" with the recognizer's own reason. Null when no testing setup declares source
    /// roles (then a role means nothing).
    /// </summary>
    public static ImportSourceRole? SourceRole(SourceSetupView? setup, Guid? snapshotId, bool sourceDetected)
    {
        if (setup is not { Packs.Count: > 0 }) return null;
        if (!sourceDetected && snapshotId is null) return new("No source", "A document-only import never satisfies a source requirement.", false);
        if (snapshotId is not { } id) return new("Not determined", "The source role is recognized from the source analysis, which does not exist yet.", false);
        var matches = setup.Packs.SelectMany(p => p.Sources).Where(s => s.Candidates.Any(c => c.SnapshotId == id)).ToList();
        if (matches.Count > 0)
        {
            var matchedOn = matches.SelectMany(s => s.Candidates.Where(c => c.SnapshotId == id).SelectMany(c => c.MatchedOn)).Distinct().Take(2).ToList();
            return new(string.Join(" · ", matches.Select(s => s.Role.Label).Distinct()),
                matchedOn.Count > 0 ? $"Recognized from the source contents: {string.Join(", ", matchedOn)}." : "Recognized from the source contents.", true);
        }
        var nearMiss = setup.Packs.SelectMany(p => p.Sources).SelectMany(s => s.NearMisses.Where(n => n.SnapshotId == id).Select(n => (s.Role.Label, n.Reason))).FirstOrDefault();
        return new("Not identified", nearMiss.Reason is { } reason
            ? $"Checked as {nearMiss.Label}: {reason}"
            : $"It matches no source requirement of {string.Join(", ", setup.Packs.Select(p => p.DisplayName))}. Shared libraries and unrelated repositories stay unidentified.", false);
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
                // The exact role that is missing ("M2LB application source missing"), and what was checked — never "M2LB: Not found".
                var text = status.State switch
                {
                    _ when SourceSetupPresentation.IsAmbiguous(status) => $"{status.Role.Label} source requires clarification",
                    TestPackSourceState.Missing => $"{status.Role.Label} source missing",
                    TestPackSourceState.ReviewRequired => $"{status.Role.Label} source binding needs review",
                    _ => $"{status.Role.Label}: {line.State}",
                };
                var expected = status.State == TestPackSourceState.Missing && status.Role.ExpectedArtifact is { Length: > 0 } artifact ? $" Expected: {artifact}." : "";
                items.Add(new($"setup-{pack.PackId}-{status.Role.Key}", text, $"{pack.DisplayName}: {SourceSetupPresentation.Sentence(status)}{expected}",
                    line.ActionLabel ?? "Review setup", line.Href ?? "#pi-setup-heading", Group: ImportAttentionGroup.TestingSetup));
            }
        if (source is { SnapshotId: null, SourceDetected: true })
            items.Add(new("source-analysis", "Source analysis was not created for this project", source.Detail, source.CanRetry ? "Retry" : "View details", "#pi-current-heading",
                Group: ImportAttentionGroup.SourceAnalysis));
        if (!environmentSelected)
            items.Add(new("test-environment", "No test environment selected", "Integration and runtime reviews need one. Document review does not.",
                "Select test environment", NavigationCatalog.TargetEnvironmentsRoute, Informational: true, Group: ImportAttentionGroup.Environment));
        return items;
    }

    /// <summary>Attention items grouped by who resolves them, in a fixed order (documents, testing setup, source analysis, environment).</summary>
    public static IReadOnlyList<(ImportAttentionGroup Group, string Title, IReadOnlyList<ImportAttentionItem> Items)> Grouped(IReadOnlyList<ImportAttentionItem> items) =>
        [.. items.GroupBy(i => i.Group).OrderBy(g => g.Key).Select(g => (g.Key, GroupTitle(g.Key), (IReadOnlyList<ImportAttentionItem>)g.ToList()))];

    public static string GroupTitle(ImportAttentionGroup group) => group switch
    {
        ImportAttentionGroup.Documents => "Project documents",
        ImportAttentionGroup.TestingSetup => "Testing setup",
        ImportAttentionGroup.SourceAnalysis => "Source analysis",
        _ => "Environment",
    };

    // ── Previously imported projects ─────────────────────────────────────────────────

    /// <summary>"24 Oct 2026".</summary>
    public static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An area status in words: "Not detected", "Not analyzed" (the snapshot predates the area).</summary>
    public static string AreaStatus(string status) => status switch
    {
        "NotDetected" => "Not detected",
        "NotAnalyzed" => "Not analyzed",
        _ => status,
    };

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
