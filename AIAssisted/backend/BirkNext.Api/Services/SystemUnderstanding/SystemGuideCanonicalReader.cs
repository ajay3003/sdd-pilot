using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.SystemUnderstanding;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.SystemUnderstanding;

/// <summary>
/// Reads System Understanding "What to test" scenarios for the canonical interpretation (MT-7). Only what the guide states is passed on: the
/// requirement ids its evidence names (GuideEvidence.RequirementId, with the specification file it cites) and the tests it names. Nothing is
/// matched by title or text; the correlation rules decide which associations are explicit and which are suggestions.
/// </summary>
public sealed class SystemGuideCanonicalReader(SystemGuideService guides) : ICanonicalGuideReader
{
    public async Task<IReadOnlyList<GuideScenarioInput>> ScenariosAsync(IReadOnlyList<IqrSourceSnapshot> interpreted, CancellationToken ct) =>
        [.. (await guides.RecognisedGuidesAsync(interpreted, ct)).SelectMany(Inputs)];

    /// <summary>One input per scenario of the revision. Pure.</summary>
    public static IEnumerable<GuideScenarioInput> Inputs(SystemGuideRevision revision)
    {
        var content = revision.Content;
        foreach (var s in content.Scenarios)
        {
            var rules = content.Rules.Where(r => r.TestScenarioIds.Contains(s.Id, StringComparer.Ordinal)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            var evidence = s.Evidence.Concat(rules.SelectMany(r => r.Evidence)).ToList();
            var tests = s.ExistingTests.Concat(evidence.Select(e => e.TestId).OfType<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            var requirements = evidence.Where(e => !string.IsNullOrWhiteSpace(e.RequirementId))
                .Select(e => new GuideRequirementReference(e.RequirementId!.Trim(), CorrelationRules.SpecScope(e.File) is null ? null : e.File)).ToList();
            yield return new GuideScenarioInput(revision.GuideKey, revision.Revision, revision.State == GuideRevisionState.Published, s.Id, s.Title,
                E.ScenarioFingerprint(content, s), [.. rules.Select(r => r.Id)], tests, requirements);
        }
    }
}
