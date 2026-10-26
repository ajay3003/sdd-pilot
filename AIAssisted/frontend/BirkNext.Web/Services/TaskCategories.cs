using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Tester-facing task categories. A task can have several (e.g. Testing + Security); none is a result or a verdict.</summary>
public enum TaskCategory { Testing, Verification, Security, Integration, Accessibility, Performance, Data, Implementation, Setup, Other }

/// <summary>
/// Deterministic task categories from the Task artifact's own wording, reusing the existing flags: Testing is the parser's testing flag (the
/// same one TASK-002 counts), Security its security flag, Architecture/Setup the topic taxonomy's infrastructure detection. Verification is a
/// task that starts with an explicit check verb (verify, confirm, validate, check, review); Accessibility, Integration, Performance and Data
/// need their explicit words. Implementation is a task that names a source file or starts with a build verb. Anything else is Other — a task
/// is never placed in a category on weaker evidence.
/// </summary>
public static partial class TaskCategories
{
    public static IReadOnlyList<TaskCategory> Of(TaskNode task)
    {
        if (task.NodeType != TaskNodeType.Task) return [];
        var text = Text(task);
        var categories = new List<TaskCategory>();
        // Creating or configuring a solution, project or package (test projects and test packages included) is setup work, not testing work.
        if (SetupLead().IsMatch(text) && SetupObject().IsMatch(text) && !VerificationLead().IsMatch(text))
            return [TaskCategory.Setup];
        if (task.IsTestingTask) categories.Add(TaskCategory.Testing);
        if (VerificationLead().IsMatch(text)) categories.Add(TaskCategory.Verification);
        if (task.IsSecurityTask) categories.Add(TaskCategory.Security);
        if (IntegrationWords().IsMatch(text)) categories.Add(TaskCategory.Integration);
        if (AccessibilityWords().IsMatch(text)) categories.Add(TaskCategory.Accessibility);
        if (PerformanceWords().IsMatch(text)) categories.Add(TaskCategory.Performance);
        if (DataWords().IsMatch(text)) categories.Add(TaskCategory.Data);
        var setup = TaskTopicTaxonomy.Detect(new TaskItem { Description = text, RelatedFileIds = task.RelatedFiles }).Contains(AffectedArea.Infrastructure);
        if (setup && !categories.Contains(TaskCategory.Testing) && !categories.Contains(TaskCategory.Verification)) categories.Add(TaskCategory.Setup);
        if (!categories.Contains(TaskCategory.Testing) && !categories.Contains(TaskCategory.Verification) && !categories.Contains(TaskCategory.Setup)
            && (task.RelatedFiles.Any(f => f.StartsWith("src/", StringComparison.OrdinalIgnoreCase)) || BuildLead().IsMatch(text)))
            categories.Add(TaskCategory.Implementation);
        if (categories.Count == 0) categories.Add(TaskCategory.Other);
        return categories;
    }

    /// <summary>Testing or verification work: the tester's primary view.</summary>
    public static bool IsTestingWork(TaskNode task) => Of(task) is var c && (c.Contains(TaskCategory.Testing) || c.Contains(TaskCategory.Verification));

    public static string Label(TaskCategory category) => category switch
    {
        TaskCategory.Testing => "Test",
        TaskCategory.Setup => "Architecture / setup",
        _ => category.ToString(),
    };

    /// <summary>The task text with the leading task ID, checkbox and markers removed, so "Verify …" is recognised at the start.</summary>
    private static string Text(TaskNode task) => Prefix().Replace(task.Title ?? "", "").Trim();

    [GeneratedRegex(@"^(?:[-*]\s*)?(?:\[[ xX]\]\s*)?(?:T\d{2,4}[a-zA-Z]?\s*)?(?:\[(?:P|US\d+[^\]]*|Story[^\]]*)\]\s*)*", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^(?:verify|verifies|confirm|validate|check|review|manually\s+(?:test|verify|check))\b", RegexOptions.IgnoreCase)]
    private static partial Regex VerificationLead();

    [GeneratedRegex(@"\b(?:integration[\s-]?tests?|end[\s-]?to[\s-]?end|e2e|contract[\s-]?tests?|api[\s-]?tests?|endpoint[\s-]?tests?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntegrationWords();

    [GeneratedRegex(@"\b(?:accessibility|accessible|a11y|wcag|aria[\s-]|screen[\s-]?readers?|keyboard[\s-]?navigation|focus[\s-]?(?:management|order|trap))", RegexOptions.IgnoreCase)]
    private static partial Regex AccessibilityWords();

    [GeneratedRegex(@"\b(?:performance|load[\s-]?tests?|latency|p95|p99|throughput|benchmarks?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PerformanceWords();

    [GeneratedRegex(@"\b(?:migrations?|database|dbcontext|schema|seed[\s-]?data)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DataWords();

    [GeneratedRegex(@"^(?:create|implement|add|update|extend|refactor|wire|register|configure|build|introduce|remove|rename|move|map|expose)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BuildLead();

    [GeneratedRegex(@"^(?:create|configure|add|scaffold|initiali[sz]e|set\s*up|install)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SetupLead();

    /// <summary>A solution, project file, NuGet/package references, or "create … project(s)"; a bare "project" in other sentences is not setup.</summary>
    [GeneratedRegex(@"(?:\.sln\b|\.csproj\b|\bsolution\b|\bnuget\b|\bpackage\s+references?\b|^(?:create|scaffold|initiali[sz]e)\b[^.]*\bprojects?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SetupObject();
}

/// <summary>A user story (or phase) group of testing/verification tasks in the Testing work view.</summary>
public sealed record TestingWorkGroup(string Title, IReadOnlyList<TaskNode> Testing, IReadOnlyList<TaskNode> Verification);

public static class TestingWork
{
    /// <summary>Testing and verification tasks grouped by user story, else phase, in document order. Only actual tasks of the artifact.</summary>
    public static IReadOnlyList<TestingWorkGroup> Group(IEnumerable<TaskNode> tasks) =>
        [.. tasks.Where(TaskCategories.IsTestingWork)
            .GroupBy(t => t.UserStoryTitle ?? (t.UserStoryTag is { } us ? us : null) ?? t.PhaseTitle ?? "Other tasks")
            .Select(g => new TestingWorkGroup(g.Key,
                [.. g.Where(t => TaskCategories.Of(t).Contains(TaskCategory.Testing))],
                [.. g.Where(t => !TaskCategories.Of(t).Contains(TaskCategory.Testing))]))];

    /// <summary>"14 tasks · 9 tests · 5 verification" for the summary.</summary>
    public static (int Total, int Testing, int Verification) Count(IEnumerable<TaskNode> tasks)
    {
        var work = tasks.Where(TaskCategories.IsTestingWork).ToList();
        var testing = work.Count(t => TaskCategories.Of(t).Contains(TaskCategory.Testing));
        return (work.Count, testing, work.Count - testing);
    }
}

/// <summary>An earlier revision of the same Task artifact offered for comparison ("Revision 1 · 24 Oct 2026 · tasks.md").</summary>
public sealed record TaskRevisionOption(string Label, string Content, string? Path = null, string? Fingerprint = null, int? Revision = null,
    DateTimeOffset? CapturedAt = null)
{
    /// <summary>"rev 2 · captured 2026-10-24 10:12 · fingerprint 3fa9c1d2e4b5 · Person/specs/001-x/tasks.md" — the exact version compared.</summary>
    public string Provenance => string.Join(" · ", new[]
    {
        Revision is { } r ? $"rev {r}" : null,
        CapturedAt is { } at ? $"captured {at.ToLocalTime():yyyy-MM-dd HH:mm}" : null,
        Fingerprint is { Length: > 0 } f ? $"fingerprint {f[..Math.Min(12, f.Length)]}" : null,
        Path,
    }.Where(p => !string.IsNullOrEmpty(p)));
}

/// <summary>
/// Earlier revisions of one Task artifact from the workspace's stored artifact revisions (what imports already record): the same project (an
/// import's project name, else the same workspace scope) and the same relative path, with different content, newest first. Another feature
/// pack's tasks.md has another path and is never offered; no separate revision store is created.
/// </summary>
public static class TaskRevisionHistory
{
    public static IReadOnlyList<TaskRevisionOption> Previous(SddLifecycleState lifecycle, BirkNext.Web.Services.Explorers.ArtifactExplorerState state, string content)
    {
        if (state.Selected is not { } selected || string.IsNullOrEmpty(content)) return [];
        var path = Normalize(selected.SourcePath ?? selected.FileName);
        var current = ArtifactFingerprint.Compute(content);
        string? ProjectOf(string? scope) => ProjectImportScope.ImportIdOf(scope) is { } id
            ? lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == id)?.ProjectName : scope;
        var project = ProjectOf(state.ProjectSlug);
        return [.. lifecycle.Revisions
            .Where(r => r.Role == nameof(WorkspaceArtifactType.Tasks) && !string.IsNullOrEmpty(r.Content)
                        && string.Equals(Normalize(r.SourceReference ?? r.FileName), path, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ProjectOf(r.WorkspaceScope), project, StringComparison.Ordinal)
                        && !string.Equals(ArtifactFingerprint.Compute(r.Content), current, StringComparison.Ordinal))
            .OrderByDescending(r => r.CapturedAt)
            .DistinctBy(r => ArtifactFingerprint.Compute(r.Content))
            .Select(r => new TaskRevisionOption($"Revision {r.Revision} · captured {r.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}", r.Content,
                r.SourceReference ?? r.FileName, r.Fingerprint is { Length: > 0 } f ? f : ArtifactFingerprint.Compute(r.Content), r.Revision, r.CapturedAt))];
    }

    /// <summary>The current Task artifact as the right-hand side of a comparison (its path, revision and content fingerprint).</summary>
    public static TaskRevisionOption? Current(BirkNext.Web.Services.Explorers.ArtifactExplorerState state, string content) =>
        state.Selected is not { } selected || string.IsNullOrEmpty(content) ? null
            : new TaskRevisionOption("Current", content, selected.SourcePath ?? selected.FileName, ArtifactFingerprint.Compute(content), selected.Revision, selected.CapturedAt);

    private static string Normalize(string? path) => (path ?? "").Replace('\\', '/').TrimStart('/');
}
