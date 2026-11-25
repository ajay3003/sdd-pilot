using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.TestCoverage;
using BirkNext.Specifications;
using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Api.Services.Specifications;

/// <summary>One imported specification file as discovery reads it, stored with the catalog revision of its import.</summary>
public sealed record StoredSpecificationEvidence(string Path, string ContentSha256, CoverageWorkspaceEvidence Evidence);

/// <summary>
/// The server-side twin of the browser's specification evidence (<see cref="TestCoveragePresentation.WorkspaceEvidence"/>): the same parser
/// (<see cref="SpecExplorerService"/>, compiled into the API) and the same mapping of user stories, acceptance scenarios, requirements and
/// clarifications, so a scenario discovered from the imported specification has the same identity as one discovered from the browser's copy.
/// Built once per specification content at Project Import, while the document text is in memory.
/// </summary>
public static class SpecificationEvidenceBuilder
{
    /// <summary>The evidence of one specification file. <paramref name="module"/> is the browser's artifact scope ("Module X") when one is selected;
    /// <paramref name="featureDocuments"/> are the import's other documents (plan.md / quickstart.md in the same folder relate stories to requirements).</summary>
    public static CoverageWorkspaceEvidence Build(string path, string content, string? module = null, IReadOnlyList<(string Path, string Text)>? featureDocuments = null)
    {
        var spec = SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(content), content);
        var specPath = path.Replace('\\', '/');
        var requirements = spec.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.Id)).GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First())
            .Select(r => new CoverageRequirement(r.Id, r.Text, null)).ToList();
        var stories = spec.UserStories.Where(u => u.IsStoryHeading).Select(u => new CoverageUserStory
        {
            Key = CoverageUserStory.KeyOf(specPath, u.StableKey), StoryId = u.AuthoredId, Ordinal = u.Ordinal, Title = u.Title, Priority = u.Priority,
            Description = u.Description, WhyThisPriority = u.Why, IndependentTest = u.IndependentTest, SpecificationPath = specPath, Module = null,
            LineStart = u.LineStart, LineEnd = u.LineEnd, Scenarios = u.LinkedAcceptanceScenarios.Count(s => s.ParentStoryKey == u.StableKey),
            NamedRequirements = NamedRequirements($"{u.Title}\n{u.Description}\n{u.Why}\n{u.IndependentTest}"),
            TaskRequirements = u.AuthoredId is { } storyTag ? DocumentRequirements(featureDocuments, specPath, storyTag) : [],
        }).ToList();
        var scenarios = spec.AcceptanceScenarios.Select((s, i) => new QaTestCandidate
        {
            Id = string.IsNullOrWhiteSpace(s.Id) ? $"scenario-{i + 1}" : s.Id!, Title = string.IsNullOrWhiteSpace(s.Title) ? s.When ?? $"Scenario {i + 1}" : s.Title,
            Source = "Acceptance scenario in the specification", Ownership = TestOwnership.QaManual,
            RequirementIds = s.LinkedRequirements.Select(r => r.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList(),
            Given = s.Given, When = s.When, Then = s.Then,
            StoryKey = s.ParentStoryKey is { } parent ? CoverageUserStory.KeyOf(specPath, parent) : null, NumberInStory = s.NumberInStory,
        }).ToList();
        return new CoverageWorkspaceEvidence
        {
            ArtifactScope = module,
            SpecificationArtifacts = [new CoverageArtifactRef(specPath, specPath, null, Sha(content))],
            SpecificationAvailable = true,
            Requirements = requirements, AcceptanceScenarios = scenarios, Stories = stories,
            // No BirkNext-recorded clarification state exists for an import yet: the documented answer decides Open/Documented, as in the browser.
            Clarifications = Clarifications(spec.Clarifications),
        };
    }

    // The three helpers below are the browser's own rules (TestCoveragePresentation.NamedRequirements / DocumentRequirements and
    // ClarificationStates.ForDiscovery with no recorded state); those browser services are not compiled into the API.

    private static readonly System.Text.RegularExpressions.Regex FrIds = new(@"(?<![\w-])(FR)-?(\d{1,4})([a-z])?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ClarificationIds = new(@"\b(FR|US|SC)-?(\d{1,4})([a-z])?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static List<string> NamedRequirements(string text) =>
        [.. FrIds.Matches(text ?? "").Select(m => BirkNext.Sdd.RequirementReferenceParser.Normalize($"FR-{m.Groups[2].Value}") + m.Groups[3].Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal)];

    public static List<StoryTaskRequirement> DocumentRequirements(IReadOnlyList<(string Path, string Text)>? documents, string? specPath, string storyTag)
    {
        if (documents is null || specPath is null || System.Text.RegularExpressions.Regex.Match(storyTag, @"\d+") is not { Success: true } number) return [];
        static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var folder = Folder(specPath.Replace('\\', '/'));
        var names = new System.Text.RegularExpressions.Regex($@"(?<![\w-])(?:US-?{number.Value}|User Story {number.Value}|Brukerhistorie {number.Value})(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var result = new List<StoryTaskRequirement>();
        foreach (var (rawPath, text) in documents)
        {
            var path = rawPath.Replace('\\', '/');
            var file = path[(path.LastIndexOf('/') + 1)..];
            // tasks.md too (2026-11-24): its "[US1]" task lines name the story's requirements — the browser's builder always read them, discovery did not,
            // so a story showed only the plan's references (SkoleService US1: FR-018 instead of FR-007, FR-014, FR-015c, FR-016, FR-018, FR-022, FR-026a).
            if (!new[] { "plan.md", "quickstart.md", "tasks.md" }.Contains(file, StringComparer.OrdinalIgnoreCase) || !string.Equals(Folder(path), folder, StringComparison.OrdinalIgnoreCase)) continue;
            // tasks.md (2026-11-25): the story's tagged task lines ("- [X] T072 [US2] …"), its phase goal and its tasks' continuation lines — as the
            // browser's task parser reads them. Not any line that mentions the story: US2's phase goal says "Processing is US1's", not a US1 task.
            if (string.Equals(file, "tasks.md", StringComparison.OrdinalIgnoreCase)) { result.AddRange(BirkNext.Sdd.StoryTaskContext.Read(path, text, storyTag, taskLines: true)); continue; }
            var lines = (text ?? "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!names.IsMatch(line)) continue;
                foreach (var fr in NamedRequirements(line)) result.Add(new StoryTaskRequirement(fr, null, line.Length > 300 ? line[..300] + "…" : line, path, i + 1, StoryTaskRequirement.PlanMention));
            }
        }
        return [.. result.DistinctBy(r => (r.RequirementId, r.TasksPath, r.Line))];
    }

    /// <summary>Clarifications as discovery reads them with no state recorded in BirkNext: a documented answer is "Documented", else "Open".</summary>
    public static List<CoverageClarification> Clarifications(IEnumerable<SemanticClarification> items) =>
        [.. items.Where(i => !string.IsNullOrWhiteSpace(i.Question)).Select(i => new CoverageClarification(
            string.IsNullOrWhiteSpace(i.Id) ? "Q-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(i.Question)))[..12] : i.Id!,
            i.Question.Trim(), string.IsNullOrWhiteSpace(i.Answer) ? null : i.Answer.Trim(),
            string.IsNullOrWhiteSpace(i.Answer) ? CoverageClarification.Open : CoverageClarification.Documented, null,
            [.. i.AffectedElements.Concat(ClarificationIds.Matches(i.Question).Select(m => m.Groups[1].Value.ToUpperInvariant() == "FR"
                    ? BirkNext.Sdd.RequirementReferenceParser.Normalize($"FR-{m.Groups[2].Value}") + m.Groups[3].Value.ToUpperInvariant() : m.Value.ToUpperInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase)]))];

    /// <summary>The evidence of every specification file among an import's documents (the shared role classifier decides which are specifications).</summary>
    public static List<StoredSpecificationEvidence> BuildAll(IEnumerable<SpecificationCatalogService.Document> documents)
    {
        var all = documents.ToList();
        var markdown = all.Where(d => d.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();
        var roles = BirkNext.Web.Services.SampleProjects.ArtifactDocumentDiscovery.Classify(
            markdown.Select(d => new BirkNext.Web.Services.SampleProjects.ArtifactDocumentDiscovery.Candidate(d.Path, System.IO.Path.GetFileName(d.Path), d.Content)));
        var specs = roles.Where(r => r.Role == WorkspaceArtifactType.Specification).Select(r => r.RelativePath).ToHashSet(StringComparer.Ordinal);
        var feature = markdown.Select(d => (d.Path, d.Content)).ToList();
        return [.. markdown.Where(d => specs.Contains(d.Path)).OrderBy(d => d.Path, StringComparer.Ordinal)
            .Select(d => new StoredSpecificationEvidence(SpecificationCatalogRules.NormalizePath(d.Path), Sha(d.Content), Build(d.Path, d.Content, null, feature)))];
    }

    /// <summary>
    /// The semantic identity of specification evidence: scenario ids with their Given/When/Then and requirement ids, and requirement ids with their
    /// wording. Two texts with the same identity give the same candidates; a browser copy with another identity is a different specification revision.
    /// </summary>
    public static string SemanticFingerprint(CoverageWorkspaceEvidence e) => Sha(JsonSerializer.Serialize(new
    {
        Scenarios = e.AcceptanceScenarios.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new { s.Id, Given = Norm(s.Given), When = Norm(s.When), Then = Norm(s.Then),
            Requirements = s.RequirementIds.Order(StringComparer.OrdinalIgnoreCase) }),
        Requirements = e.Requirements.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new { r.Id, Text = Norm(r.Text) }),
    }));

    private static string? Norm(string? text) => string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
