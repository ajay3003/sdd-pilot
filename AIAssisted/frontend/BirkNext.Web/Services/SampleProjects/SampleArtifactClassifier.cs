using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services.SampleProjects;

public enum ArtifactConfidence
{
    Unknown,
    Possible,
    Strong,
    Confirmed,
}

public enum ArtifactDiscoveryStatus
{
    /// <summary>One supported role, Strong or Confirmed. Explorers may use it.</summary>
    Detected,
    /// <summary>Weak evidence only, or several roles fit about equally well. Shown for review; never used automatically.</summary>
    NeedsReview,
    /// <summary>A readable document with no reliable supported role (README, checklist, contract, notes …).</summary>
    Unclassified,
    /// <summary>The document could not be read or parsed. Distinct from "not found".</summary>
    ParseError,
}

public sealed record ArtifactRoleCandidate(
    WorkspaceArtifactType Role,
    ArtifactConfidence Confidence,
    int Score,
    IReadOnlyList<string> Reasons);

public sealed record ArtifactClassification(
    ArtifactDiscoveryStatus Status,
    WorkspaceArtifactType? Role,
    ArtifactConfidence Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ArtifactRoleCandidate> Candidates);

/// <summary>
/// Deterministic artifact-role classifier for a Markdown document (no AI, no I/O). Input: relative path and text. Output:
/// a role with confidence and reasons, a "needs review" state with role candidates, or "unclassified".
///
/// Precedence:
///   1. explicit front-matter metadata (type/artifact/documentType/role/kind) → Confirmed
///   2. exact canonical filename (spec.md, plan.md …) → strong hint; Confirmed when the content agrees
///   3. document structure from the shared Markdown tokenizer and the domain extractors (Specification, Task,
///      Data Model, Constitution, Plan) → Strong when the evidence is substantial
///   4. filename synonyms and folder names → only supporting hints, never sufficient on their own
/// A canonical filename contradicted by strong content of another role, or two roles with close strong evidence, is
/// "needs review" — the classifier never picks one arbitrarily. Filename is never the sole determinant except for the
/// exact canonical names, which were the previous contract and stay supported.
/// </summary>
public static class SampleArtifactClassifier
{
    /// <summary>Product display order of supported roles.</summary>
    public static readonly IReadOnlyList<WorkspaceArtifactType> RoleOrder =
    [
        WorkspaceArtifactType.Constitution,
        WorkspaceArtifactType.Specification,
        WorkspaceArtifactType.DataModel,
        WorkspaceArtifactType.Plan,
        WorkspaceArtifactType.Tasks,
        WorkspaceArtifactType.Research,
    ];

    private const int CanonicalFilenameScore = 4;
    private const int SynonymFilenameScore = 2;
    private const int PathScore = 1;

    private static readonly Dictionary<string, WorkspaceArtifactType> CanonicalFilenames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["constitution"] = WorkspaceArtifactType.Constitution,
        ["spec"] = WorkspaceArtifactType.Specification,
        ["specification"] = WorkspaceArtifactType.Specification,
        ["data-model"] = WorkspaceArtifactType.DataModel,
        ["plan"] = WorkspaceArtifactType.Plan,
        ["tasks"] = WorkspaceArtifactType.Tasks,
        ["research"] = WorkspaceArtifactType.Research,
    };

    private static readonly Dictionary<string, WorkspaceArtifactType> SynonymFilenames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["requirements"] = WorkspaceArtifactType.Specification,
        ["functional-specification"] = WorkspaceArtifactType.Specification,
        ["requirements-specification"] = WorkspaceArtifactType.Specification,
        ["feature-spec"] = WorkspaceArtifactType.Specification,
        ["implementation-plan"] = WorkspaceArtifactType.Plan,
        ["delivery-plan"] = WorkspaceArtifactType.Plan,
        ["project-plan"] = WorkspaceArtifactType.Plan,
        ["task-list"] = WorkspaceArtifactType.Tasks,
        ["tasklist"] = WorkspaceArtifactType.Tasks,
        ["work-items"] = WorkspaceArtifactType.Tasks,
        ["backlog"] = WorkspaceArtifactType.Tasks,
        ["datamodel"] = WorkspaceArtifactType.DataModel,
        ["data_model"] = WorkspaceArtifactType.DataModel,
        ["database-model"] = WorkspaceArtifactType.DataModel,
        ["domain-model"] = WorkspaceArtifactType.DataModel,
        ["entity-model"] = WorkspaceArtifactType.DataModel,
        ["project-constitution"] = WorkspaceArtifactType.Constitution,
        ["governance"] = WorkspaceArtifactType.Constitution,
        ["principles"] = WorkspaceArtifactType.Constitution,
        ["analysis"] = WorkspaceArtifactType.Research,
        ["findings"] = WorkspaceArtifactType.Research,
        ["investigation"] = WorkspaceArtifactType.Research,
    };

    private static readonly Dictionary<string, WorkspaceArtifactType> FolderHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["specs"] = WorkspaceArtifactType.Specification,
        ["spec"] = WorkspaceArtifactType.Specification,
        ["specifications"] = WorkspaceArtifactType.Specification,
        ["requirements"] = WorkspaceArtifactType.Specification,
        ["plans"] = WorkspaceArtifactType.Plan,
        ["planning"] = WorkspaceArtifactType.Plan,
        ["tasks"] = WorkspaceArtifactType.Tasks,
        ["backlog"] = WorkspaceArtifactType.Tasks,
        ["data-model"] = WorkspaceArtifactType.DataModel,
        ["datamodel"] = WorkspaceArtifactType.DataModel,
        ["schema"] = WorkspaceArtifactType.DataModel,
        ["research"] = WorkspaceArtifactType.Research,
        ["governance"] = WorkspaceArtifactType.Constitution,
    };

    private static readonly Dictionary<string, WorkspaceArtifactType> MetadataValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["constitution"] = WorkspaceArtifactType.Constitution,
        ["governance"] = WorkspaceArtifactType.Constitution,
        ["spec"] = WorkspaceArtifactType.Specification,
        ["specification"] = WorkspaceArtifactType.Specification,
        ["requirements"] = WorkspaceArtifactType.Specification,
        ["feature-spec"] = WorkspaceArtifactType.Specification,
        ["plan"] = WorkspaceArtifactType.Plan,
        ["implementation-plan"] = WorkspaceArtifactType.Plan,
        ["task"] = WorkspaceArtifactType.Tasks,
        ["tasks"] = WorkspaceArtifactType.Tasks,
        ["task-list"] = WorkspaceArtifactType.Tasks,
        ["data-model"] = WorkspaceArtifactType.DataModel,
        ["datamodel"] = WorkspaceArtifactType.DataModel,
        ["domain-model"] = WorkspaceArtifactType.DataModel,
        ["research"] = WorkspaceArtifactType.Research,
    };

    private static readonly HashSet<string> MetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "artifact", "artifacttype", "artifact-type", "artifact_type", "documenttype", "document-type", "document_type",
        "doctype", "role", "kind",
    };

    /// <summary>Repository-level files that describe a project rather than being an SDD artifact.</summary>
    private static readonly HashSet<string> NonArtifactStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme", "changelog", "license", "contributing", "code_of_conduct", "code-of-conduct", "security", "agents", "claude",
    };

    private static readonly Regex CheckboxRe = new(@"^\s*[-*+]\s+\[[ xX]\]\s+", RegexOptions.Compiled | RegexOptions.Multiline);

    private sealed record RoleSignals(Regex Title, Regex Heading, int HeadingCap);

    private static readonly Dictionary<WorkspaceArtifactType, RoleSignals> Signals = new()
    {
        [WorkspaceArtifactType.Specification] = new(
            new(@"\b(feature\s+)?spec(ification)?\b|\brequirements?\s+spec", RegexOptions.IgnoreCase),
            new(@"\b(functional|non-functional)\s+requirements\b|\bacceptance\s+(criteria|scenarios)\b|\buser\s+(stor(y|ies)|scenarios)\b|\bsuccess\s+criteria\b|\bkey\s+entities\b|\bedge\s+cases\b", RegexOptions.IgnoreCase), 3),
        [WorkspaceArtifactType.Plan] = new(
            new(@"\b(implementation|delivery|project|release|migration)\s+plan\b|^plan\b", RegexOptions.IgnoreCase),
            new(@"\btechnical\s+context\b|\bproject\s+structure\b|\bimplementation\s+(approach|strategy|steps|phases)\b|\bmilestones?\b|\bconstitution\s+check\b|\barchitecture\s+(approach|overview)\b|\bsequencing\b|\brollout\b", RegexOptions.IgnoreCase), 3),
        [WorkspaceArtifactType.Tasks] = new(
            new(@"^tasks?\b|\btask\s+list\b|\bwork\s+items\b", RegexOptions.IgnoreCase),
            new(@"\bdependencies\s+&\s+execution\s+order\b|\bparallel\s+(opportunities|execution)\b|\btasks?\s+for\b", RegexOptions.IgnoreCase), 2),
        [WorkspaceArtifactType.DataModel] = new(
            new(@"\bdata\s*-?\s*model\b|\bdomain\s+model\b|\bdatabase\s+(model|schema|design)\b|\bentity\s+model\b|\bER\s+(model|diagram)\b", RegexOptions.IgnoreCase),
            new(@"\bentit(y|ies)\b|\brelationships?\b|\btables?\b|\bfields\b|\bcolumns\b|\bschema\b|\benums?\b|\bindexes\b", RegexOptions.IgnoreCase), 2),
        [WorkspaceArtifactType.Constitution] = new(
            new(@"\bconstitution\b|\bgovernance\b|\bguiding\s+principles\b", RegexOptions.IgnoreCase),
            new(@"\bcore\s+principles\b|\bprinciples\b|\bgovernance\b|\bnon-negotiable\b|\bamendment", RegexOptions.IgnoreCase), 2),
        [WorkspaceArtifactType.Research] = new(
            new(@"\bresearch\b|\binvestigation\b|\bspike\b|\bfindings\b", RegexOptions.IgnoreCase),
            new(@"\bdecision\b|\balternatives(\s+considered)?\b|\btrade-?offs?\b|\bfindings\b|\brationale\b|\boptions\s+(considered|evaluated)\b", RegexOptions.IgnoreCase), 3),
    };

    private static readonly DataModelAnalysisService DataModelExtractor = new();
    private static readonly ConstitutionAnalysisService ConstitutionExtractor = new();
    private static readonly PlanAnalysisService PlanExtractor = new();

    public static ArtifactClassification Classify(string relativePath, string? content)
    {
        if (content is null)
            return new(ArtifactDiscoveryStatus.ParseError, null, ArtifactConfidence.Unknown, ["The document could not be read."], []);
        try
        {
            return ClassifyCore(relativePath, Normalize(content));
        }
        catch (Exception ex)
        {
            return new(ArtifactDiscoveryStatus.ParseError, null, ArtifactConfidence.Unknown, [$"The document could not be parsed ({ex.GetType().Name})."], []);
        }
    }

    private static ArtifactClassification ClassifyCore(string relativePath, string text)
    {
        var path = relativePath.Replace('\\', '/');
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var stem = StripExtension(fileName);
        var folders = path.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToList();

        // 1. Explicit metadata.
        var (frontMatter, body) = SplitFrontMatter(text);
        foreach (var (key, value) in frontMatter)
        {
            if (!MetadataKeys.Contains(key)) continue;
            if (MetadataValues.TryGetValue(value.Trim().Trim('"', '\''), out var declared))
                return new(ArtifactDiscoveryStatus.Detected, declared, ArtifactConfidence.Confirmed,
                    [$"Front matter declares {key}: {value.Trim()}"], [new(declared, ArtifactConfidence.Confirmed, 100, [$"Front matter {key}: {value.Trim()}"])]);
        }

        if (NonArtifactStems.Contains(stem))
            return new(ArtifactDiscoveryStatus.Unclassified, null, ArtifactConfidence.Unknown,
                [$"{fileName} is a repository description file, not an artifact role."], []);

        var tokens = MarkdownTokenizer.Tokenize(body);
        var headings = tokens.Where(t => t.Kind == MarkdownTokenKind.Heading).ToList();
        var title = headings.FirstOrDefault(h => h.HeadingLevel == 1)?.Content ?? headings.FirstOrDefault()?.Content ?? string.Empty;

        if (title.Contains("checklist", StringComparison.OrdinalIgnoreCase) || stem.Contains("checklist", StringComparison.OrdinalIgnoreCase)
            || folders.Any(f => f.Equals("checklists", StringComparison.OrdinalIgnoreCase)))
            return new(ArtifactDiscoveryStatus.Unclassified, null, ArtifactConfidence.Unknown,
                ["Checklist document — validates other artifacts; not an artifact role BirkNext explores."], []);

        if (title.StartsWith("contract", StringComparison.OrdinalIgnoreCase) || folders.Any(f => f.Equals("contracts", StringComparison.OrdinalIgnoreCase)))
            return new(ArtifactDiscoveryStatus.Unclassified, null, ArtifactConfidence.Unknown,
                ["API/integration contract document — no dedicated explorer."], []);

        var content = new Dictionary<WorkspaceArtifactType, (int Score, List<string> Reasons)>();
        foreach (var role in RoleOrder) content[role] = (0, []);

        void Add(WorkspaceArtifactType role, int score, string reason)
        {
            var (s, r) = content[role];
            r.Add(reason);
            content[role] = (s + score, r);
        }

        // 3. Structure: title, headings, extractor evidence.
        foreach (var (role, signals) in Signals)
        {
            if (title.Length > 0 && signals.Title.IsMatch(title)) Add(role, 3, $"Title “{Shorten(title)}”");
            var matched = headings.Skip(1).Select(h => h.Content).Where(h => signals.Heading.IsMatch(h))
                .Select(h => Shorten(h)).Distinct(StringComparer.OrdinalIgnoreCase).Take(signals.HeadingCap).ToList();
            if (matched.Count > 0) Add(role, matched.Count, $"Heading{(matched.Count == 1 ? "" : "s")} {string.Join(", ", matched.Select(m => $"“{m}”"))}");
        }

        var spec = SpecExplorerService.Parse(body);
        var specItems = Flatten(spec.Roots, n => n.Children).Count(n => n.NodeType is SpecNodeType.Requirement or SpecNodeType.SuccessCriterion
            or SpecNodeType.AcceptanceTest or SpecNodeType.BddScenario or SpecNodeType.UserStory);
        if (specItems >= 3) Add(WorkspaceArtifactType.Specification, 3, $"{specItems} requirement/scenario items (Specification extractor)");
        else if (specItems > 0) Add(WorkspaceArtifactType.Specification, 1, $"{specItems} requirement/scenario item{(specItems == 1 ? "" : "s")} (Specification extractor)");

        var tasks = TaskExplorerService.Parse(body);
        var taskIds = Flatten(tasks.Roots, n => n.Children).Where(n => n.TaskId is not null).Select(n => n.TaskId).Distinct().Count();
        if (taskIds >= 3) Add(WorkspaceArtifactType.Tasks, 4, $"{taskIds} task IDs (Task extractor)");
        else if (taskIds > 0) Add(WorkspaceArtifactType.Tasks, 1, $"{taskIds} task ID{(taskIds == 1 ? "" : "s")} (Task extractor)");
        var checkboxes = CheckboxRe.Matches(body).Count;
        if (checkboxes >= 5) Add(WorkspaceArtifactType.Tasks, 1, $"{checkboxes} checkbox items");

        var dataModel = DataModelExtractor.Parse(body);
        var entities = dataModel.Entities.Count(e => e.Columns.Count > 0);
        if (entities >= 2) Add(WorkspaceArtifactType.DataModel, 3, $"{entities} entities with fields (Data Model extractor)");
        else if (entities == 1) Add(WorkspaceArtifactType.DataModel, 1, "1 entity with fields (Data Model extractor)");

        var constitution = ConstitutionExtractor.Parse(body);
        if (constitution.Principles.Count >= 3) Add(WorkspaceArtifactType.Constitution, 2, $"{constitution.Principles.Count} principles (Constitution extractor)");

        var plan = PlanExtractor.Parse(body);
        var planItems = plan.Milestones.Count + plan.ConstitutionCheckItems.Count + plan.ArchitectureDecisions.Count;
        if (planItems >= 2) Add(WorkspaceArtifactType.Plan, 2, $"{planItems} milestones/decisions/constitution checks (Plan extractor)");

        // 2 + 4. Filename and folder hints.
        var hints = RoleOrder.ToDictionary(r => r, _ => (Score: 0, Reasons: new List<string>()));
        WorkspaceArtifactType? canonical = CanonicalFilenames.TryGetValue(stem, out var c) ? c : null;
        if (canonical is { } cr) { hints[cr].Reasons.Add($"Exact canonical filename {fileName}"); hints[cr] = (CanonicalFilenameScore, hints[cr].Reasons); }
        else if (SynonymFilenames.TryGetValue(stem, out var sr)) { hints[sr].Reasons.Add($"Filename {fileName}"); hints[sr] = (SynonymFilenameScore, hints[sr].Reasons); }
        foreach (var folder in folders)
            if (FolderHints.TryGetValue(folder, out var fr) && hints[fr].Score < CanonicalFilenameScore + PathScore && !hints[fr].Reasons.Any(r => r.StartsWith("Folder", StringComparison.Ordinal)))
            {
                hints[fr].Reasons.Add($"Folder {folder}/");
                hints[fr] = (hints[fr].Score + PathScore, hints[fr].Reasons);
            }

        var candidates = RoleOrder
            .Select(r =>
            {
                var contentScore = content[r].Score;
                // A folder name alone is not evidence: it only supports a role the content or filename already suggests.
                var hintScore = contentScore > 0 || canonical == r || hints[r].Reasons.Any(x => x.StartsWith("Filename", StringComparison.Ordinal))
                    ? hints[r].Score : 0;
                var reasons = (hintScore > 0 ? hints[r].Reasons : []).Concat(content[r].Reasons).ToList();
                return (Role: r, Content: contentScore, Total: contentScore + hintScore, Reasons: reasons);
            })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total).ThenBy(x => RoleOrder.ToList().IndexOf(x.Role))
            .ToList();

        IReadOnlyList<ArtifactRoleCandidate> ToCandidates(ArtifactConfidence top) => candidates
            .Select((x, i) => new ArtifactRoleCandidate(x.Role, i == 0 ? top : x.Content >= 4 ? ArtifactConfidence.Strong : ArtifactConfidence.Possible, x.Total, x.Reasons))
            .ToList();

        if (candidates.Count == 0 || candidates[0].Total < 3)
            return new(ArtifactDiscoveryStatus.Unclassified, null, ArtifactConfidence.Unknown,
                candidates.Count == 0
                    ? ["No supported artifact-role structure found."]
                    : [$"Only weak signals (best: {Label(candidates[0].Role)}) — not enough to assign a role."],
                ToCandidates(ArtifactConfidence.Unknown));

        var best = candidates[0];
        var runnerUp = candidates.Count > 1 ? candidates[1] : default;

        // Canonical filename contradicted by strong content of another role.
        if (canonical is { } named && best.Role != named && best.Content >= 4 && content[named].Score < 2)
            return new(ArtifactDiscoveryStatus.NeedsReview, null, ArtifactConfidence.Possible,
                [$"Filename suggests {Label(named)}, but the content looks like {Label(best.Role)}."], ToCandidates(ArtifactConfidence.Possible));

        // Two roles with substantial content evidence, the weaker at least two thirds of the stronger.
        if (runnerUp.Total > 0 && runnerUp.Content >= 4 && runnerUp.Total * 3 >= best.Total * 2 && canonical != best.Role)
            return new(ArtifactDiscoveryStatus.NeedsReview, null, ArtifactConfidence.Possible,
                [$"Multiple candidate roles: {Label(best.Role)} and {Label(runnerUp.Role)} fit about equally well."], ToCandidates(ArtifactConfidence.Possible));

        var confidence = canonical == best.Role
            ? best.Content >= 3 ? ArtifactConfidence.Confirmed : ArtifactConfidence.Strong
            : best.Content >= 4 ? ArtifactConfidence.Strong
            : best.Content >= 3 && hints[best.Role].Reasons.Any(r => r.StartsWith("Filename", StringComparison.Ordinal)) ? ArtifactConfidence.Strong
            : ArtifactConfidence.Possible;

        return confidence == ArtifactConfidence.Possible
            ? new(ArtifactDiscoveryStatus.NeedsReview, null, ArtifactConfidence.Possible,
                [$"Possibly {Label(best.Role)}, but the evidence is weak."], ToCandidates(ArtifactConfidence.Possible))
            : new(ArtifactDiscoveryStatus.Detected, best.Role, confidence, best.Reasons, ToCandidates(confidence));
    }

    public static string Label(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.DataModel => "Data Model",
        _ => role.ToString(),
    };

    private static string Normalize(string text)
    {
        if (text.StartsWith('﻿')) text = text[1..];
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static (List<(string Key, string Value)> Pairs, string Body) SplitFrontMatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return ([], text);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return ([], text);
        var pairs = new List<(string, string)>();
        foreach (var line in text[4..end].Split('\n').Take(50))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith(' ') || line.StartsWith('\t')) continue;
            pairs.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        var bodyStart = text.IndexOf('\n', end + 1);
        return (pairs, bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..]);
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private static string Shorten(string s)
    {
        s = s.Replace("*", "").Replace("`", "").Trim();
        return s.Length > 60 ? s[..57] + "…" : s;
    }

    private static IEnumerable<T> Flatten<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children)
    {
        var stack = new Stack<T>(roots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in children(node).Reverse()) stack.Push(child);
        }
    }
}
