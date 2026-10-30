using System.IO.Compression;
using System.Text;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>Small M2LB-like Spec-Kit repositories as ZIP bytes. Entry times are fixed, so the same files always give the same bytes.</summary>
internal static class DeliveryReportingFixtures
{
    public static readonly DateTimeOffset EntryTime = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    public static byte[] Zip(IEnumerable<(string Path, string Content)> files, string? zipComment = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path);
                entry.LastWriteTime = EntryTime;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        var bytes = stream.ToArray();
        if (zipComment is null) return bytes;
        // ZipArchive (.NET 8) cannot write an archive comment: set the end-of-central-directory comment length and append it, as git archive does.
        var comment = Encoding.ASCII.GetBytes(zipComment);
        bytes[^2] = (byte)(comment.Length & 0xFF);
        bytes[^1] = (byte)(comment.Length >> 8);
        return [.. bytes, .. comment];
    }

    public static string Spec(string title, params string[] acceptance) => $"""
        # Feature Specification: {title}

        **Feature Branch**: `001-x`
        **Created**: 2026-10-01
        **Status**: Draft

        ## User Scenarios & Testing *(mandatory)*

        ### User Story 1 - {title} (Priority: P1)

        A caseworker uses {title}.

        **Acceptance Scenarios**:

        {string.Join("\n", acceptance.Select((a, i) => $"{i + 1}. {a}"))}

        ## Requirements *(mandatory)*

        - **FR-001**: System MUST support {title}.
        """;

    public static readonly string[] DefaultAcceptance =
    [
        "**Given** a caseworker, **When** they search by name, **Then** matching children are listed",
        "**Given** no match, **When** they search, **Then** an empty result is shown",
    ];

    public static string Tasks(int total, int completed, string? statusLine = null)
    {
        var sb = new StringBuilder("# Tasks\n\n");
        if (statusLine is not null) sb.Append($"**Status**: {statusLine}\n\n");
        sb.Append("## Phase 1: Setup\n\n");
        for (var i = 1; i <= total; i++) sb.Append($"- [{(i <= completed ? "x" : " ")}] T{i:000} Implement step {i} of the feature\n");
        return sb.ToString();
    }

    public static (string, string)[] ChildSearch(int total, int completed, string module = "Frontend", string[]? acceptance = null, string? statusLine = null) =>
    [
        ($"{module}/specs/001-child-search/spec.md", Spec("Child search", acceptance ?? DefaultAcceptance)),
        ($"{module}/specs/001-child-search/tasks.md", Tasks(total, completed, statusLine)),
    ];

    public static ParsedDeliverySnapshot Parse(IEnumerable<(string Path, string Content)> files, string name = "m2lb.zip") =>
        DeliverySnapshotParser.Parse(name, Zip(files)).Snapshot!;

    /// <summary>A parsed snapshot as the comparison reads it after storage.</summary>
    public static DeliverySnapshotContent Content(ParsedDeliverySnapshot parsed, DateTimeOffset effectiveAt, string label = "snapshot") => new()
    {
        Ref = new DeliveryReportSnapshotRef
        {
            SnapshotId = Guid.NewGuid(), Label = label, EffectiveAt = effectiveAt, ImportedAt = effectiveAt, SourceType = parsed.SourceType,
            SourceFileName = parsed.FileName, Fingerprint = parsed.Fingerprint, FeatureCount = parsed.Features.Count,
        },
        Coverage = parsed.Coverage,
        Warnings = parsed.Warnings,
        Features = parsed.Features.Select(f => new DeliveryContentFeature
        {
            Key = f.Key, Folder = f.Folder, Module = BirkNext.Web.Services.Explorers.ArtifactModules.Label(f.Module), Title = f.Title, Position = f.Position,
            SpecPath = f.SpecPath, TasksPath = f.TasksPath, PlanPath = f.PlanPath, SpecFingerprint = f.SpecFingerprint,
            AcceptanceFingerprint = f.AcceptanceFingerprint, AcceptanceCriteria = f.AcceptanceCriteria, Status = f.Status, StatusReason = f.StatusReason,
            TaskTotal = f.TaskTotal, TaskCompleted = f.TaskCompleted,
            Tasks = f.Tasks.Select(t => new DeliveryContentTask(t.TaskKey, t.Text, t.Completed) { TaskId = t.TaskId, PhaseKey = t.PhaseKey, UserStoryKey = t.UserStoryKey, SourcePath = t.SourcePath }).ToList(),
            HierarchyCaptured = true, Phases = [.. f.Phases.Select(p => new DeliveryContentPhase(p.Key, p.Title, p.Position))],
            Stories = [.. f.Stories.Select(s => new DeliveryContentStory(s.Key, s.Title, s.Description, s.Priority, s.PhaseKeys, s.Position, s.Line))],
            Criteria = [.. f.Criteria.Select(c => new DeliveryContentCriterion(c.Key, c.Text, c.StoryKey, c.Position))],
        }).ToList(),
        SourceFiles = parsed.Artifacts.Where(a => a.Kind == "Source")
            .ToDictionary(a => a.Path, a => (BirkNext.Web.Services.Explorers.ArtifactModules.Label(a.Module), a.Fingerprint), StringComparer.Ordinal),
    };

    public static readonly DateTimeOffset Monday = new(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(1));
    public static readonly DateTimeOffset Friday = new(2026, 10, 30, 15, 0, 0, TimeSpan.FromHours(1));
}
