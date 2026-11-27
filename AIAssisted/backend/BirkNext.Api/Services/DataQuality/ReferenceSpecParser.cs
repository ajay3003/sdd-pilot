using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.DataQuality;

/// <summary>
/// An INDEPENDENT reference reading of a specification document for the data-quality audit (2026-11-27). It shares no code with the production
/// parser (<c>SpecExplorerService</c>) on purpose: where the two disagree, the audit shows both readings and their records; neither is presumed right.
/// Rules (documented in the report):
/// <list type="bullet">
/// <item>An acceptance scenario is a NUMBERED list item that contains a Given/Gitt marker, inside an «Acceptance Scenarios»/«Akseptansescenarioer»/
/// «Akseptansekriterier» block of a user story. A Given marker elsewhere (Edge Cases, notes) is counted separately and is NOT a scenario.</item>
/// <item>A user story whose heading is struck through or marked Removed/Fjernet/Withdrawn/Utgått is withdrawn: its scenarios are counted as withdrawn.</item>
/// <item>A requirement is a list item that starts with a bold id (<c>- **FR-001**</c>); the id is normalized (upper case, ASCII hyphen).</item>
/// <item>A requirement is «without a scenario» when no acceptance scenario of the same document names its id.</item>
/// </list>
/// </summary>
public static partial class ReferenceSpecParser
{
    public sealed record Scenario(int Line, int? Story, int Number, string Text, IReadOnlyList<string> RequirementIds);
    public sealed record Reading(IReadOnlyList<Scenario> Scenarios, IReadOnlyList<string> Requirements, IReadOnlyList<string> RequirementsWithoutScenario,
        int GivenOutsideAcceptance, int WithdrawnScenarios, int Stories);

    [GeneratedRegex(@"^\s{0,3}(#{2,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(?:~~)?\s*(?:User\s+Story|Brukerhistorie)\s+(\d+)", RegexOptions.IgnoreCase)] private static partial Regex StoryHeading();
    [GeneratedRegex(@"~~|\b(Removed|Fjernet|Withdrawn|Utgått)\b", RegexOptions.IgnoreCase)] private static partial Regex Withdrawn();
    [GeneratedRegex(@"acceptance\s+scenarios|akseptansescenario|akseptansekriteri", RegexOptions.IgnoreCase)] private static partial Regex AcceptanceLabel();
    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"\b(Given|Gitt)\b", RegexOptions.IgnoreCase)] private static partial Regex GivenMarker();
    [GeneratedRegex(@"^\s*[-*+]\s+\*\*\s*((?:FR|NFR)\s*[-‐‑–—]?\s*\d{1,4}[a-z]?)\s*\*\*", RegexOptions.IgnoreCase)] private static partial Regex RequirementItem();
    [GeneratedRegex(@"(?<![\w-])((?:FR|NFR))\s*[-‐‑–—]?\s*(\d{1,4})([a-z]?)\b", RegexOptions.IgnoreCase)] private static partial Regex RequirementMention();

    public static string NormalizeRequirementId(string raw)
    {
        var m = RequirementMention().Match(raw.Replace("*", "").Replace("`", ""));
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}{m.Groups[3].Value.ToLowerInvariant()}" : raw.Trim().ToUpperInvariant();
    }

    public static Reading Read(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var scenarios = new List<Scenario>();
        var requirements = new List<string>();
        int? story = null; var storyLevel = 0; var withdrawnStory = false; var inAcceptance = false; var givenOutside = 0; var withdrawnScenarios = 0; var stories = 0;
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;
            if (Heading().Match(line) is { Success: true } h)
            {
                var level = h.Groups[1].Value.Length; var text = h.Groups[2].Value.Replace("*", "");
                if (StoryHeading().Match(text.Trim()) is { Success: true } s)
                {
                    story = int.Parse(s.Groups[1].Value); storyLevel = level; withdrawnStory = Withdrawn().IsMatch(h.Groups[2].Value); stories++;
                    inAcceptance = false; continue;
                }
                if (story is not null && level <= storyLevel) { story = null; withdrawnStory = false; }
                inAcceptance = story is not null && AcceptanceLabel().IsMatch(text);
                continue;
            }
            if (story is not null && AcceptanceLabel().IsMatch(line) && line.Contains("**", StringComparison.Ordinal) && !Numbered().IsMatch(line)) { inAcceptance = true; continue; }
            if (RequirementItem().Match(line) is { Success: true } r) { requirements.Add(NormalizeRequirementId(r.Groups[1].Value)); }
            if (!GivenMarker().IsMatch(line)) continue;
            if (inAcceptance && Numbered().Match(line) is { Success: true } n)
            {
                if (withdrawnStory) { withdrawnScenarios++; continue; }
                var mentions = RequirementMention().Matches(line).Select(m => NormalizeRequirementId(m.Value)).Distinct(StringComparer.Ordinal).ToList();
                scenarios.Add(new Scenario(i + 1, story, int.Parse(n.Groups[1].Value), n.Groups[2].Value.Trim(), mentions));
            }
            else givenOutside++;
        }
        var distinct = requirements.Distinct(StringComparer.Ordinal).ToList();
        var named = scenarios.SelectMany(s => s.RequirementIds).ToHashSet(StringComparer.Ordinal);
        return new(scenarios, distinct, [.. distinct.Where(id => !named.Contains(id))], givenOutside, withdrawnScenarios, stories);
    }
}
