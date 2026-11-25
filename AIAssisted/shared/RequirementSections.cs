using System.Text.RegularExpressions;

namespace BirkNext.Sdd;

/// <summary>
/// One functional requirement as the specification places it: the section it is written under (the source's own functional area), with the
/// identifier exactly as written next to the canonical comparison id. Source-derived only: no section is guessed; an FR outside any section has
/// <see cref="Section"/> null ("Ukjent").
/// </summary>
/// <param name="RequirementId">Canonical comparison id (<see cref="RequirementReferenceParser.Normalize"/>, e.g. FR-027A): stable lookup key.</param>
/// <param name="DisplayId">The identifier exactly as the source writes it (e.g. FR-027a): for display only, never for identity.</param>
/// <param name="SectionPath">Section titles from the requirements heading inward (a bold line or a sub-heading; nested headings give several).</param>
/// <param name="Line">1-based line of the requirement's definition.</param>
/// <param name="SectionLine">1-based line of the innermost section title; null without a section.</param>
public sealed record RequirementSectionEntry(string RequirementId, string DisplayId, string? Section, IReadOnlyList<string> SectionPath, int Line, int? SectionLine);

/// <summary>A functional area as the source defines it: a section title under the requirements heading, with the requirements it holds (may be empty).</summary>
public sealed record RequirementSection(string Title, IReadOnlyList<string> Path, int Line, IReadOnlyList<string> RequirementIds);

public sealed record RequirementSectionMap(IReadOnlyList<RequirementSectionEntry> Requirements, IReadOnlyList<RequirementSection> Sections)
{
    public static readonly RequirementSectionMap Empty = new([], []);

    private Dictionary<string, RequirementSectionEntry>? _byId;

    /// <summary>The entry of a requirement by canonical id (case-insensitive); null when the specification does not define it.</summary>
    public RequirementSectionEntry? Find(string requirementId)
    {
        _byId ??= Requirements.GroupBy(r => r.RequirementId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byId.TryGetValue(RequirementReferenceParser.Normalize(requirementId), out var e) ? e : null;
    }

    /// <summary>The source spelling of a requirement id (FR-027a) when this specification defines it; otherwise the id as given.</summary>
    public string DisplayId(string requirementId) => Find(requirementId)?.DisplayId ?? requirementId;
}

/// <summary>
/// Reads the functional areas of a specification from its own structure (2026-11-24): inside a requirements heading ("Functional Requirements",
/// "Requirements", "Krav" …), a bold-only line (<c>**Intake and identity**</c>) or a deeper heading starts a section; each requirement definition
/// (<c>- **FR-001**: …</c>, <c>**FR-001**: …</c>, <c>#### FR-001 …</c>) belongs to the innermost open section. A bold line that is itself a
/// requirement or a label with text (<c>**Note**: …</c>) is never a section; a heading at the requirements heading's level or above ends the region.
/// </summary>
public static partial class RequirementSections
{
    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    // A bold-only line: "**Intake and identity**" (an optional trailing colon). Never one with text after it.
    [GeneratedRegex(@"^\*\*(?<title>[^*]{2,120})\*\*\s*:?\s*$")]
    private static partial Regex BoldLine();

    // A requirement definition at the start of a line: list item or paragraph, bold or plain, or a heading.
    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+\.\s+)?(?:#{1,6}\s+)?\**\s*(?<id>(?:FR|NFR|REQ|SR-FR)-?\d{1,4}[A-Za-z]?)\b\**\s*[:.\-—–]", RegexOptions.IgnoreCase)]
    private static partial Regex Definition();

    [GeneratedRegex(@"\b(?:FR|NFR|REQ)-?\d{1,4}[A-Za-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnyId();

    [GeneratedRegex(@"\b(functional\s+requirements?|requirements?|krav|funksjonelle\s+krav)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementsHeading();

    public static RequirementSectionMap Read(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return RequirementSectionMap.Empty;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var entries = new List<RequirementSectionEntry>();
        var sections = new List<(string Title, List<string> Path, int Line, List<string> Ids)>();
        int? regionLevel = null;            // level of the open requirements heading
        var stack = new List<(int Level, string Title, int Line)>(); // sections inside the region (headings: their level; bold lines: region level + 10)
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;
            if (Heading().Match(line) is { Success: true } h)
            {
                var level = h.Groups["hashes"].Value.Length;
                var title = Clean(h.Groups["title"].Value);
                if (regionLevel is { } rl && level <= rl) { regionLevel = null; stack.Clear(); }
                if (regionLevel is null)
                {
                    if (RequirementsHeading().IsMatch(title) && !Definition().IsMatch(line)) { regionLevel = level; stack.Clear(); }
                    continue;
                }
                if (Definition().IsMatch(line)) { AddEntry(i, line); continue; } // "#### FR-001 …" is a requirement, not a section
                // "### Functional Requirements" under "## Requirements" narrows the region (a sibling such as "### Key Entities" then ends it);
                // it is never a functional area of its own.
                if (stack.Count == 0 && RequirementsHeading().IsMatch(title)) { regionLevel = level; continue; }
                stack.RemoveAll(s => s.Level >= level);
                stack.Add((level, title, i + 1));
                sections.Add((title, [.. stack.Select(s => s.Title)], i + 1, []));
                continue;
            }
            if (regionLevel is null) continue;
            if (BoldLine().Match(line) is { Success: true } b && !AnyId().IsMatch(line))
            {
                var title = Clean(b.Groups["title"].Value);
                // A bold section sits below the innermost heading; it replaces a previous bold section at the same depth.
                stack.RemoveAll(s => s.Level >= 100);
                stack.Add((100, title, i + 1));
                sections.Add((title, [.. stack.Select(s => s.Title)], i + 1, []));
                continue;
            }
            // A definition starts its line (a continuation line of a list item is indented and never starts with an id).
            if (raw.Length - raw.TrimStart().Length <= 1 && Definition().IsMatch(line)) AddEntry(i, line);
        }
        return new RequirementSectionMap(
            [.. entries.DistinctBy(e => e.RequirementId, StringComparer.OrdinalIgnoreCase)],
            [.. sections.Select(s => new RequirementSection(s.Title, s.Path, s.Line, s.Ids))]);

        void AddEntry(int index, string line)
        {
            var display = Definition().Match(line).Groups["id"].Value;
            if (display.StartsWith("SR-", StringComparison.OrdinalIgnoreCase)) return; // a source reference ("[SR-FR-001]"), not this spec's requirement
            display = Regex.Replace(display, @"^(?<p>[A-Za-z]+)(?<n>\d)", "${p}-${n}"); // "FR001" → "FR-001"; the suffix keeps its case
            var id = RequirementReferenceParser.Normalize(display);
            var current = stack.Count > 0 ? stack[^1] : ((int Level, string Title, int Line)?)null;
            entries.Add(new(id, display, current?.Title, [.. stack.Select(s => s.Title)], index + 1, current?.Line));
            if (current is not null) sections.FindLast(s => s.Line == current.Value.Line).Ids.Add(id);
        }
    }

    private static string Clean(string title) => Regex.Replace(title.Replace("*", "").Trim().TrimEnd(':').Trim(), @"\s+", " ");
}

/// <summary>
/// The parts of a feature's tasks.md that relate a story to requirements beyond its tagged task lines (2026-11-25). The real SkoleService US2 names
/// its only requirement (FR-008a) in its phase's «Goal», and SkoleService task bodies name requirements in indented sub-bullets and «As built» notes,
/// which a line-by-line reader never saw:
/// <list type="bullet">
/// <item><b>Phase goal</b> (<see cref="BirkNext.TestCoverage.StoryTaskRequirement.PhaseGoal"/>): the «Goal» line inside the phase whose heading names the
/// story ("## Phase 4: User Story 2 …"). The story's own stated purpose: structural evidence like a task line.</item>
/// <item><b>Task detail</b> (<see cref="BirkNext.TestCoverage.StoryTaskRequirement.TaskDetail"/>): an indented continuation line of a task tagged with the
/// story ("- [X] T072 [US2] …" followed by "  - … (FR-008a)"). Supporting only: it shows a related area, never the primary one.</item>
/// </list>
/// Only ids written as this specification's own (FR-008a), never a prefixed reference of another service (UT-FR-001, SR-FR-013).
/// </summary>
public static partial class StoryTaskContext
{
    [GeneratedRegex(@"(?<![\w-])FR-?(?<n>\d{1,4})(?<s>[a-z])?\b", RegexOptions.IgnoreCase)]
    private static partial Regex FrId();

    [GeneratedRegex(@"^#{2,3}\s+(?:Phase\s+\d+\s*:\s*)?(?:User Story|Brukerhistorie)\s+(?<n>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StoryPhase();

    [GeneratedRegex(@"^#{1,2}\s")]
    private static partial Regex TopHeading();

    [GeneratedRegex(@"^\*\*(?:Goal|Mål)\*\*\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex GoalLine();

    [GeneratedRegex(@"^[-*]\s+\[[ xX]\]\s+(?<id>T\d+)\b(?<rest>.*)$")]
    private static partial Regex TaskLine();

    /// <param name="taskLines">Also the tagged task lines themselves (<see cref="BirkNext.TestCoverage.StoryTaskRequirement.TaskLine"/>): the API's
    /// builder has no task parser; the browser reads tagged lines through its task explorer and asks for the context only.</param>
    public static List<BirkNext.TestCoverage.StoryTaskRequirement> Read(string tasksPath, string? text, string storyTag, bool taskLines = false)
    {
        var result = new List<BirkNext.TestCoverage.StoryTaskRequirement>();
        if (string.IsNullOrWhiteSpace(text) || Regex.Match(storyTag ?? "", @"\d+") is not { Success: true } number) return result;
        var tag = new Regex($@"\[(?:US|Brukerhistorie\s*)-?{number.Value}\]", RegexOptions.IgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inPhase = false; string? task = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i]; var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                task = null;
                if (StoryPhase().Match(line) is { Success: true } phase) inPhase = phase.Groups["n"].Value == number.Value;
                else if (TopHeading().IsMatch(line)) inPhase = false; // the next "## Phase …" ends this story's phase; "### Tests for …" does not
                continue;
            }
            if (line.Length == 0) continue;
            var indented = raw.Length - raw.TrimStart().Length >= 2;
            if (!indented && TaskLine().Match(line) is { Success: true } t)
            {
                task = tag.IsMatch(t.Groups["rest"].Value) ? t.Groups["id"].Value : null;
                if (task is not null && taskLines) Add(line, i, task, BirkNext.TestCoverage.StoryTaskRequirement.TaskLine);
                continue;
            }
            if (!indented) task = null;
            if (inPhase && GoalLine().IsMatch(line)) Add(line, i, null, BirkNext.TestCoverage.StoryTaskRequirement.PhaseGoal);
            else if (task is not null && indented) Add(line, i, task, BirkNext.TestCoverage.StoryTaskRequirement.TaskDetail);
        }
        return [.. result.DistinctBy(r => (r.RequirementId, r.Line))];

        void Add(string line, int index, string? taskId, string kind)
        {
            foreach (Match m in FrId().Matches(line))
                result.Add(new(RequirementReferenceParser.Normalize($"FR-{m.Groups["n"].Value}") + m.Groups["s"].Value.ToUpperInvariant(), taskId,
                    line.Length > 300 ? line[..300] + "…" : line, tasksPath, index + 1, kind));
        }
    }
}

/// <summary>A reference to an acceptance scenario as a feature's documents write it: "US2-3", "User Story 3 scenario 3", "US2 scenarios 2–3".</summary>
public sealed record SourceScenarioReference(string StoryId, int Number, string Text);

/// <summary>A scenario reference resolved within ONE specification (2026-11-25): resolved only when that specification has the story and the numbered
/// scenario; "US3-14" in another feature never resolves to this specification's US3. Unresolved references are kept with their reason.</summary>
public sealed record ResolvedScenarioReference(SourceScenarioReference Reference, string SpecificationPath, bool Resolved, string? Reason);

/// <summary>
/// Scenario references of the Spec Kit project convention used in M2LB (2026-11-25; e.g. MeldingsutvekslerReceiver tasks.md "US5-2", SkoleService
/// tasks.md "US2 scenarios 2–3"). Read verbatim; ranges expand to their members; an id inside another namespace ("TT-US1-2") is not read.
/// </summary>
public static partial class SourceScenarioReferences
{
    [GeneratedRegex(@"(?<![\w-])US(?<s>\d+)-(?<n>\d+)\b")]
    private static partial Regex Dash();

    [GeneratedRegex(@"(?<![\w-])(?:US(?<s>\d+)|User Story (?<s>\d+)),?\s+scenarios?\s+(?<a>\d+)(?:\s*(?:[–-]|to)\s*(?<b>\d+))?(?<more>(?:\s*(?:,|and|og)\s*\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex Words();

    public static List<SourceScenarioReference> Parse(string? text)
    {
        var result = new List<SourceScenarioReference>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in Dash().Matches(text)) result.Add(new($"US{m.Groups["s"].Value}", int.Parse(m.Groups["n"].Value), m.Value));
        foreach (Match m in Words().Matches(text))
        {
            var story = $"US{m.Groups["s"].Value}";
            var a = int.Parse(m.Groups["a"].Value);
            var b = m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value) : a;
            if (b < a || b - a > 50) b = a;
            for (var n = a; n <= b; n++) result.Add(new(story, n, m.Value));
            foreach (Match extra in Regex.Matches(m.Groups["more"].Value, @"\d+")) result.Add(new(story, int.Parse(extra.Value), m.Value));
        }
        return [.. result.DistinctBy(r => (r.StoryId, r.Number))];
    }

    /// <param name="scenarioCount">The number of acceptance scenarios a story of THIS specification has (null = the story does not exist here).</param>
    public static List<ResolvedScenarioReference> Resolve(IEnumerable<SourceScenarioReference> references, string specificationPath, Func<string, int?> scenarioCount) =>
        [.. references.Select(r => scenarioCount(r.StoryId) switch
        {
            null => new ResolvedScenarioReference(r, specificationPath, false, $"{r.StoryId} finnes ikke i denne spesifikasjonen."),
            var count when r.Number < 1 || r.Number > count => new ResolvedScenarioReference(r, specificationPath, false, $"{r.StoryId} har {count} scenarioer her; {r.Text} peker utenfor."),
            _ => new ResolvedScenarioReference(r, specificationPath, true, null),
        })];
}
