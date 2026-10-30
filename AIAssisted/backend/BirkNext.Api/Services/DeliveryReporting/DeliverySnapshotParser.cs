using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.DeliveryReporting;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Api.Services.DeliveryReporting;

public sealed record ParsedDeliveryTask(string TaskKey, string? TaskId, string Text, bool Completed, string? Phase, string? UserStory, int Position, string SourcePath);

public sealed record ParsedDeliveryEvidence(string Kind, string Path, int Line, string Text);

public sealed record ParsedDeliveryFeature
{
    public string Key { get; init; } = "";
    public string Module { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Title { get; init; } = "";
    public int Position { get; init; }
    public string? SpecPath { get; init; }
    public string? TasksPath { get; init; }
    public string? PlanPath { get; init; }
    public string? SpecFingerprint { get; init; }
    public string? AcceptanceFingerprint { get; init; }
    public List<string> AcceptanceCriteria { get; init; } = [];
    public int RequirementCount { get; init; }
    public int UserStoryCount { get; init; }
    public int OpenClarifications { get; init; }
    /// <summary>Explicit status declared in the feature's own spec.md/tasks.md ("**Status**: Ready for test"), if recognized.</summary>
    public DeliveryStatus? ExplicitStatus { get; init; }
    /// <summary>Checkbox tasks only. Bare "T001 …" lines carry no completion state and are counted as untracked.</summary>
    public List<ParsedDeliveryTask> Tasks { get; init; } = [];
    public int UntrackedTaskCount { get; init; }
    public List<ParsedDeliveryEvidence> Evidence { get; init; } = [];
    public DeliveryStatus Status { get; init; }
    public string StatusReason { get; init; } = "";
    public int TaskTotal => Tasks.Count;
    public int TaskCompleted => Tasks.Count(t => t.Completed);
}

public sealed record ParsedDeliveryArtifact(string Path, string Module, string? FeatureKey, string Kind, string Fingerprint);

public sealed record ParsedDeliverySnapshot
{
    public string FileName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public long ArchiveBytes { get; init; }
    public long EntryCount { get; init; }
    public DeliverySourceType SourceType { get; init; }
    public string? GitCommit { get; init; }
    public string? WrapperFolder { get; init; }
    public DateTimeOffset? SuggestedEffectiveAt { get; init; }
    public string? SuggestedEffectiveReason { get; init; }
    public List<string> Modules { get; init; } = [];
    public List<ParsedDeliveryFeature> Features { get; init; } = [];
    public List<ParsedDeliveryArtifact> Artifacts { get; init; } = [];
    public List<DeliveryImportWarning> Warnings { get; init; } = [];
    public DeliveryCoverage Coverage { get; init; }
}

public sealed record DeliverySnapshotParseResult(ParsedDeliverySnapshot? Snapshot, SourceArchiveValidationFailure? Failure);

/// <summary>
/// Turns one uploaded ZIP into a normalized delivery snapshot, once, at import. Reuses the canonical bounded archive reader (validation, limits,
/// ignored folders), the canonical module resolver (<see cref="ArtifactModules"/>: Spec-Kit structure only, so documentation trees never become
/// modules) and the existing Spec/Task explorer parsers. Pure: no database, no BirkNext state; the same bytes always give the same snapshot.
/// <para>A feature is a Spec-Kit feature folder (<c>&lt;module&gt;/specs/&lt;feature&gt;/</c>) holding spec.md and/or tasks.md. spec.md gives
/// identity, title and acceptance criteria; tasks.md checkboxes give task progress; plan.md is recorded as context only.</para>
/// </summary>
public static class DeliverySnapshotParser
{
    private static readonly Regex CheckboxRe = new(@"^\s*[-*+]\s+\[([ xX])\]", RegexOptions.Compiled);
    private static readonly Regex StatusLineRe = new(@"^\s*(?:[-*]\s+)?\*{0,2}(?:Delivery\s+)?Status\*{0,2}\s*:\s*\*{0,2}\s*(.+?)\s*\*{0,2}\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClarificationRe = new(@"\[NEEDS CLARIFICATION", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TitlePrefixRe = new(@"^(?:Feature\s+Specification|Feature\s+Spec|Specification|Feature)\s*[:\-–]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitCommitRe = new(@"^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static readonly Regex FeatureNumberRe = new(@"^(\d{1,4})[-_]", RegexOptions.Compiled);

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".razor", ".sql", ".json", ".yaml", ".yml", ".tf", ".bicep", ".graphql", ".xsd", ".proto", ".props" };

    public static DeliverySnapshotParseResult Parse(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var read = IqrSourceArchiveReader.ReadDetailed(fileName, bytes, captureDocuments: true, ct);
        if (!read.IsValid) return new(null, read.Failure);
        var workspace = read.Workspace!;
        var warnings = new List<DeliveryImportWarning>();

        var documents = workspace.DocumentFiles ?? [];
        var sourceFiles = workspace.Files.Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? []).ToList();
        var wrapper = WrapperFolder(workspace.AllPaths ?? documents.Select(d => d.Path).ToList());
        string Strip(string path) => wrapper is null ? path : path[(wrapper.Length + 1)..];

        var docs = documents.Select(d => (Path: Strip(d.Path), d.Content)).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        var roots = ArtifactModules.RootsOf(docs.Select(d => d.Path));
        foreach (var skipped in workspace.SkippedDocuments ?? [])
            warnings.Add(new("DOCUMENT_SKIPPED", $"A document was not read ({skipped.Reason}).", Strip(skipped.Path)));

        // Feature folders: <module>/specs/<feature>/<Spec-Kit document>. Loose or nested documentation never forms a feature.
        var featureDocs = new Dictionary<string, Dictionary<string, (string Path, string Content)>>(StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            var name = System.IO.Path.GetFileName(doc.Path).ToLowerInvariant();
            if (ArtifactModules.StrongRoot(doc.Path) is null || ArtifactModules.FeatureOf(doc.Path, roots) is not { } folder) continue;
            var module = ArtifactModules.ModuleOf(doc.Path, roots);
            var key = FeatureKey(module, folder);
            if (!featureDocs.TryGetValue(key, out var files)) featureDocs[key] = files = new(StringComparer.Ordinal);
            files[name] = doc;
        }
        // tasks.md outside a feature folder cannot be attributed to a story.
        foreach (var doc in docs.Where(d => System.IO.Path.GetFileName(d.Path).Equals("tasks.md", StringComparison.OrdinalIgnoreCase)))
            if (ArtifactModules.StrongRoot(doc.Path) is null || ArtifactModules.FeatureOf(doc.Path, roots) is null)
                warnings.Add(new("TASKS_UNMAPPED", "Task file cannot be mapped to a feature folder; its tasks are not counted.", doc.Path));

        var features = new List<ParsedDeliveryFeature>();
        var position = 0;
        foreach (var (key, files) in featureDocs.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            files.TryGetValue("spec.md", out var spec);
            if (spec.Path is null) files.TryGetValue("specification.md", out spec);
            files.TryGetValue("tasks.md", out var tasks);
            files.TryGetValue("plan.md", out var plan);
            if (spec.Path is null && tasks.Path is null) continue; // plan/research only: supporting documentation, not a story
            var anyPath = spec.Path ?? tasks.Path!;
            var module = ArtifactModules.ModuleOf(anyPath, roots);
            var folder = ArtifactModules.FeatureOf(anyPath, roots)!;
            features.Add(ParseFeature(key, module, folder, position++, spec.Path is null ? null : spec, tasks.Path is null ? null : tasks, plan.Path, warnings));
        }

        foreach (var feature in features.Where(f => f.SpecPath is null))
            warnings.Add(new("NO_SPECIFICATION", $"\"{feature.Title}\" has tasks but no spec.md; its title comes from the folder name.", feature.TasksPath));
        foreach (var feature in features.Where(f => f.TasksPath is null))
            warnings.Add(new("NO_TASKS", $"\"{feature.Title}\" has no tasks.md; task progress is not known.", feature.SpecPath));
        foreach (var group in features.Where(f => FeatureNumberRe.IsMatch(f.Folder))
                     .GroupBy(f => (f.Module, Number: int.Parse(FeatureNumberRe.Match(f.Folder).Groups[1].Value))).Where(g => g.Count() > 1))
            warnings.Add(new("DUPLICATE_FEATURE_ID", $"Feature number {group.Key.Number:000} is used by more than one feature folder in {ArtifactModules.Label(group.Key.Module)}: {string.Join(", ", group.Select(f => f.Folder))}."));
        if (features.Count == 0)
            warnings.Add(new("NO_FEATURES", "No Spec-Kit feature folder (specs/<feature>/spec.md or tasks.md) was found."));
        else if (features.All(f => f.SpecPath is null))
            warnings.Add(new("NO_CANONICAL_SPEC", "No canonical spec.md found; features are identified by folder name only."));

        var featureByFolder = features.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var artifacts = new List<ParsedDeliveryArtifact>();
        foreach (var doc in docs)
        {
            var module = ArtifactModules.ModuleOf(doc.Path, roots);
            string? featureKey = ArtifactModules.StrongRoot(doc.Path) is not null && ArtifactModules.FeatureOf(doc.Path, roots) is { } folder
                && featureByFolder.ContainsKey(FeatureKey(module, folder)) ? FeatureKey(module, folder) : null;
            var kind = featureKey is null ? "SupportingDocument" : System.IO.Path.GetFileNameWithoutExtension(doc.Path).ToLowerInvariant() switch
            {
                "spec" or "specification" => "Specification",
                "tasks" => "Tasks",
                "plan" => "Plan",
                _ => "FeatureDocument",
            };
            artifacts.Add(new(doc.Path, module, featureKey, kind, Hash(Normalize(doc.Content))));
        }
        foreach (var file in sourceFiles.Select(f => (Path: Strip(f.Path), f.Content)).OrderBy(f => f.Path, StringComparer.Ordinal))
            if (SourceExtensions.Contains(System.IO.Path.GetExtension(file.Path)))
                artifacts.Add(new(file.Path, ArtifactModules.ModuleOf(file.Path, roots), null, "Source", Hash(Normalize(file.Content))));

        var commit = GitCommit(bytes);
        var (suggested, suggestedReason) = SuggestedEffective(workspace.EntryModified, commit);
        var modules = roots.Select(r => ArtifactModules.Label(r)).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        var coverage = features.Count == 0 ? DeliveryCoverage.Insufficient
            : warnings.Any(w => w.Code is "DOCUMENT_SKIPPED" or "SPEC_UNREADABLE" or "TASKS_UNREADABLE") ? DeliveryCoverage.Partial
            : DeliveryCoverage.Complete;

        return new(new ParsedDeliverySnapshot
        {
            FileName = IqrSourceArchiveReader.ArchiveDisplayName(fileName),
            Fingerprint = workspace.Archive.Sha256,
            ArchiveBytes = bytes.Length,
            EntryCount = workspace.EntryCount,
            SourceType = commit is not null ? DeliverySourceType.GitArchive : DeliverySourceType.ZipUpload,
            GitCommit = commit,
            WrapperFolder = wrapper,
            SuggestedEffectiveAt = suggested,
            SuggestedEffectiveReason = suggestedReason,
            Modules = modules,
            Features = features,
            Artifacts = artifacts,
            Warnings = warnings,
            Coverage = coverage,
        }, null);
    }

    public static string FeatureKey(string module, string folder) => (module.Length == 0 ? "" : module.ToLowerInvariant() + "::") + folder.ToLowerInvariant();

    private static ParsedDeliveryFeature ParseFeature(string key, string module, string folder, int position, (string Path, string Content)? spec,
        (string Path, string Content)? tasks, string? planPath, List<DeliveryImportWarning> warnings)
    {
        var evidence = new List<ParsedDeliveryEvidence>();
        string title = Humanize(folder);
        List<string> acceptance = [];
        int requirements = 0, stories = 0, clarifications = 0;
        DeliveryStatus? explicitStatus = null;

        if (spec is { } s)
        {
            var lines = Lines(s.Content);
            if (lines.Select(l => l.Text).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal)) is { } h1)
            {
                var heading = TitlePrefixRe.Replace(StripMarkdown(h1[2..]), "").Trim();
                if (heading.Length > 0) title = heading.Length > 300 ? heading[..300] : heading;
            }
            foreach (var (line, number) in lines)
                if (ClarificationRe.IsMatch(line))
                {
                    clarifications += ClarificationRe.Matches(line).Count;
                    if (evidence.Count(e => e.Kind == "Clarification") < 20) evidence.Add(new("Clarification", s.Path, number, Truncate(StripMarkdown(line), 300)));
                }
            explicitStatus = ExplicitStatus(lines, s.Path, evidence);
            try
            {
                var tree = SpecExplorerService.Parse(s.Content);
                var nodes = Flatten(tree.Roots, n => n.Children).ToList();
                acceptance = nodes.Where(n => n.NodeType is SpecNodeType.BddScenario or SpecNodeType.AcceptanceTest or SpecNodeType.SuccessCriterion)
                    .Select(n => Collapse(StripMarkdown(n.FullContent ?? n.Title))).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                requirements = nodes.Count(n => n.NodeType == SpecNodeType.Requirement);
                stories = nodes.Count(n => n.NodeType == SpecNodeType.UserStory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(new("SPEC_UNREADABLE", $"The specification of \"{title}\" could not be fully read; acceptance criteria are not compared.", s.Path));
            }
        }

        var parsedTasks = new List<ParsedDeliveryTask>();
        var untracked = 0;
        if (tasks is { } t)
        {
            explicitStatus ??= ExplicitStatus(Lines(t.Content), t.Path, evidence);
            try
            {
                var tree = TaskExplorerService.Parse(t.Content);
                var keys = new Dictionary<string, int>(StringComparer.Ordinal);
                var order = 0;
                foreach (var node in Flatten(tree.Roots, n => n.Children).Where(n => n.NodeType == TaskNodeType.Task))
                {
                    var checkbox = CheckboxRe.Match(node.RawText);
                    if (!checkbox.Success) { untracked++; continue; }
                    var text = Collapse(node.Title);
                    var baseKey = node.TaskId ?? "text:" + Hash(text.ToLowerInvariant())[..16];
                    var taskKey = keys.TryGetValue(baseKey, out var seen) ? $"{baseKey}#{seen + 1}" : baseKey;
                    keys[baseKey] = seen + 1;
                    parsedTasks.Add(new(taskKey, node.TaskId, Truncate(text, 2000), checkbox.Groups[1].Value is "x" or "X",
                        node.PhaseTitle is { } p ? Truncate(p, 400) : null, node.UserStoryTag, order++, t.Path));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(new("TASKS_UNREADABLE", $"The task list of \"{title}\" could not be read; task progress is not known.", t.Path));
            }
        }

        var specFingerprint = spec is { } sp ? Hash(Normalize(sp.Content)) : null;
        var acceptanceFingerprint = spec is null ? null : Hash(string.Join("\n", acceptance.OrderBy(a => a, StringComparer.Ordinal)));
        var feature = new ParsedDeliveryFeature
        {
            Key = key, Module = module, Folder = folder, Title = title, Position = position,
            SpecPath = spec?.Path, TasksPath = tasks?.Path, PlanPath = planPath,
            SpecFingerprint = specFingerprint, AcceptanceFingerprint = acceptanceFingerprint, AcceptanceCriteria = acceptance,
            RequirementCount = requirements, UserStoryCount = stories, OpenClarifications = clarifications, ExplicitStatus = explicitStatus,
            Tasks = parsedTasks, UntrackedTaskCount = untracked, Evidence = evidence,
        };
        var (status, reason) = DeliveryStatusDerivation.Derive(feature.TasksPath is not null, feature.TaskTotal, feature.TaskCompleted,
            feature.UntrackedTaskCount, feature.ExplicitStatus, feature.OpenClarifications);
        return feature with { Status = status, StatusReason = reason };
    }

    /// <summary>A recognized "Status: …" line in the first lines of a feature document. Unknown values (Draft, In progress) are ignored, because
    /// only explicit delivery statuses add information beyond task checkboxes.</summary>
    private static DeliveryStatus? ExplicitStatus(List<(string Text, int Line)> lines, string path, List<ParsedDeliveryEvidence> evidence)
    {
        foreach (var (line, number) in lines.Take(40))
        {
            var match = StatusLineRe.Match(line);
            if (!match.Success) continue;
            var value = Collapse(StripMarkdown(match.Groups[1].Value)).Trim('`', '.', ' ').ToLowerInvariant();
            DeliveryStatus? status = value switch
            {
                "ready for test" or "ready-for-test" or "ready for testing" or "ready for qa" => DeliveryStatus.ReadyForTest,
                "testing" or "in test" or "in testing" or "in qa" => DeliveryStatus.Testing,
                "blocked" => DeliveryStatus.Blocked,
                "done" or "delivered" or "released" or "accepted" => DeliveryStatus.Done,
                _ => null,
            };
            if (status is null) continue;
            evidence.Add(new("StatusLine", path, number, Truncate(StripMarkdown(line).Trim(), 300)));
            return status;
        }
        return null;
    }

    /// <summary>A single top-level folder that holds every file (GitHub "Download ZIP", <c>git archive --prefix</c>) is an archive wrapper, unless
    /// it is itself Spec-Kit structure (it directly holds specs/ or .specify/).</summary>
    private static string? WrapperFolder(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return null;
        var tops = paths.Select(p => p.Split('/')).ToList();
        if (tops.Any(s => s.Length < 2)) return null;
        var top = tops[0][0];
        if (!tops.All(s => s[0].Equals(top, StringComparison.Ordinal))) return null;
        if (tops.Any(s => s[1].Equals("specs", StringComparison.OrdinalIgnoreCase) || s[1].Equals(".specify", StringComparison.OrdinalIgnoreCase))) return null;
        return top;
    }

    /// <summary>The commit id <c>git archive</c> (and GitHub's ZIP download) writes as the ZIP archive comment, when present.</summary>
    public static string? GitCommit(byte[] bytes)
    {
        // End of central directory: signature 0x06054b50, comment length at +20, comment at +22. Scan backwards over the maximum comment size.
        var min = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        for (var i = bytes.Length - 22; i >= min; i--)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b || bytes[i + 2] != 0x05 || bytes[i + 3] != 0x06) continue;
            var length = bytes[i + 20] | bytes[i + 21] << 8;
            if (i + 22 + length > bytes.Length) return null;
            var comment = Encoding.ASCII.GetString(bytes, i + 22, length).Trim();
            return GitCommitRe.IsMatch(comment) ? comment : null;
        }
        return null;
    }

    private static (DateTimeOffset?, string?) SuggestedEffective(IReadOnlyDictionary<string, DateTimeOffset>? modified, string? commit)
    {
        if (modified is null || modified.Count == 0) return (null, null);
        var distinct = modified.Values.Select(v => v.UtcDateTime).Distinct().ToList();
        // git archive stamps every entry with the commit time; varied times are ordinary file times and say nothing about the snapshot date.
        if (distinct.Count != 1) return (null, null);
        return (new DateTimeOffset(distinct[0], TimeSpan.Zero), commit is null
            ? "Every file in the archive carries the same time, as in a Git export. Check it before importing."
            : "Every file carries the same time as Git commit " + commit[..10] + ". Check it before importing.");
    }

    private static IEnumerable<T> Flatten<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(children(root), children)) yield return child;
        }
    }

    private static List<(string Text, int Line)> Lines(string content)
    {
        var result = new List<(string, int)>();
        var fenced = false;
        var number = 0;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            number++;
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (!fenced) result.Add((raw, number));
        }
        return result;
    }

    /// <summary>Line endings, trailing whitespace and trailing blank lines do not change a document's fingerprint.</summary>
    internal static string Normalize(string content) =>
        string.Join("\n", content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string StripMarkdown(string text) =>
        Regex.Replace(Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1"), @"(\*\*|__|`|\*|_)", "").Trim();

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    private static string Humanize(string folder)
    {
        var name = FeatureNumberRe.Replace(folder, "").Replace('-', ' ').Replace('_', ' ').Trim();
        if (name.Length == 0) name = folder;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
