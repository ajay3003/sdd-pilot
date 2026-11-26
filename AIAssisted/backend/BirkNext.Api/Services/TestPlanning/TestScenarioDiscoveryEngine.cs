using System.Text.Json;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.SystemUnderstanding;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;
using BirkNext.TestPlanning;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>A provider's contribution: its capability state, its candidates and (existing tests) explicit requirement → test links.</summary>
public sealed record CandidateProviderOutput(DiscoveryCapability Capability, IReadOnlyList<DiscoveredScenario> Candidates, IReadOnlyList<RequirementTestLink>? TestLinks = null,
    IReadOnlyList<ScenarioTestLink>? ScenarioLinks = null);

/// <summary>A test that names a user story acceptance scenario (story id + number) within its module (the first folder of its path).</summary>
public sealed record ScenarioTestLink(string Module, string StoryId, int Number, CandidateTestAssociation Test);

/// <summary>An existing test that names a requirement id explicitly (trait or reference in its source).</summary>
public sealed record RequirementTestLink(string RequirementId, CandidateTestAssociation Test);

/// <summary>
/// Pure normalisation of registered analyzers' output into <see cref="DiscoveredScenario"/> candidates (rule version <see cref="RuleVersion"/>):
/// stable project-scoped ids, type-specific templates, expected behaviour only where the evidence states it, deduplication only on the canonical
/// identity, and "possible duplicate" notes (never merges) for candidates that share a requirement id. No I/O.
/// </summary>
public static class TestScenarioDiscoveryEngine
{
    public const string RuleVersion = "tsd-1";
    /// <summary>
    /// A safety limit, not a working size (2026-11-26): 2,000 dropped 15 of M2LB's 2,015 candidates. Finn og forstå shows the list in batches
    /// (ShowMore) and the payload is compact, so a real project is kept whole; a result above this limit says so on the page (NotKept).
    /// </summary>
    public const int MaxCandidates = 10000;

    public const string Specification = "specification";
    public const string SystemGuide = "system-guide";
    public const string UiSource = "ui-source";
    public const string ApiContract = "api-contract";
    public const string IntegrationJourneys = "integration-journeys";
    public const string ExistingTests = "existing-tests";
    public const string DeveloperTests = "developer-tests";
    public const string TestSpecification = "test-specification";

    /// <summary>"tsc-" + 16 hex of project | capability | canonical key. The snapshot is not part of it: the same scenario keeps its id across imports.</summary>
    public static string CandidateId(string projectKey, string capability, string canonicalKey) => "tsc-" + E.Sha($"{projectKey.Trim()}|{capability}|{canonicalKey}")[..16];

    private static string Clip(string? text, int max) => string.IsNullOrWhiteSpace(text) ? "" : text.Trim().Length <= max ? text.Trim() : text.Trim()[..(max - 1)] + "…";
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Gives a candidate of another provider its stable id, fingerprint and bounded texts (the same rule as every built-in source).</summary>
    public static DiscoveredScenario FinishCandidate(DiscoveredScenario c, string projectKey) => Finish(c, projectKey);

    private static DiscoveredScenario Finish(DiscoveredScenario c, string projectKey) => c with
    {
        CandidateId = CandidateId(projectKey, c.CapabilityId, c.CanonicalKey), ProjectKey = projectKey, RuleVersion = RuleVersion,
        SourceIdentity = c.SourceIdentity.Length > 0 ? c.SourceIdentity : SourceIdentityOf(c.CapabilityId, c.CanonicalKey),
        Title = Clip(c.Title, 300), Purpose = Clip(c.Purpose, 1000),
        Fingerprint = E.Sha(JsonSerializer.Serialize(new { c.Title, c.Purpose, c.Type, c.ExpectedBehaviour, c.Preconditions, c.Steps, c.Template, c.TestDataNeeds, c.RequirementIds }, E.Json)),
    };

    /// <summary>Project-independent identity of a candidate (capability + canonical key): the same scenario in another import of the application.</summary>
    public static string SourceIdentityOf(string capability, string canonicalKey) => "tss-" + E.Sha($"{capability}|{canonicalKey}")[..16];

    /// <summary>One capability's state over every source of a basis: Available if any source had evidence, else Failed if one failed (so a failure
    /// is never read as "no evidence"), else NoEvidence, else NotApplicable. Details are kept per source.</summary>
    public static DiscoveryCapability MergeCapabilities(IReadOnlyList<DiscoveryCapability> outputs)
    {
        if (outputs.Count == 1) return outputs[0];
        var state = outputs.Any(o => o.State == DiscoveryCapabilityState.Available) ? DiscoveryCapabilityState.Available
            : outputs.Any(o => o.State == DiscoveryCapabilityState.Failed) ? DiscoveryCapabilityState.Failed
            : outputs.Any(o => o.State == DiscoveryCapabilityState.NoEvidence) ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.NotApplicable;
        var first = outputs[0];
        return first with
        {
            State = state, Candidates = outputs.Sum(o => o.Candidates), Detail = string.Join(" | ", outputs.Select(o => o.Detail).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal)),
            Limitations = [.. outputs.SelectMany(o => o.Limitations).Distinct(StringComparer.Ordinal).Take(5)],
            // A failure in one source stays visible even when another source had evidence.
            PartialFailure = outputs.Any(o => o.State == DiscoveryCapabilityState.Failed),
        };
    }

    public static DiscoveryCapability Capability(string id, DiscoveryCapabilityState state, int candidates, string detail, string engine, IEnumerable<string>? limitations = null) => new()
    {
        Id = id, Label = Label(id), Description = Description(id), State = state, Candidates = candidates, Detail = detail, Engine = engine, Limitations = [.. (limitations ?? []).Take(5)],
    };

    public static string Label(string capability) => capability switch
    {
        Specification => "Business scenarios from the specification",
        SystemGuide => "System Understanding guide scenarios",
        UiSource => "UI functional candidates from frontend source",
        ApiContract => "API / contract candidates",
        IntegrationJourneys => "Integration journeys",
        ExistingTests => "Existing tests",
        DeveloperTests => "Behaviour from developer integration tests",
        TestSpecification => "Documented end-to-end tests",
        _ => capability,
    };

    public static string Description(string capability) => capability switch
    {
        Specification => "Acceptance scenarios (Given / When / Then) and requirements (FR-001 …) of the project's selected specification.",
        SystemGuide => "Scenarios of a System Understanding guide whose sources are this project's application source.",
        UiSource => "Pages, forms, validation, search and actions found in Razor / HTML source (React and Angular templates are not analyzed).",
        ApiContract => "Operations of OpenAPI and GraphQL contracts found in the project's source.",
        IntegrationJourneys => "Journeys declared for this project, and test-pack journeys whose sources are this project's source.",
        ExistingTests => "xUnit tests in the project's source, associated with candidates only through explicit requirement references.",
        DeveloperTests => "For a project without a specification: developer tests that cross a real boundary (browser, hosted API or messaging), one candidate per test class.",
        TestSpecification => "End-to-end tests written in the modules' test specification documents (TEST-E-01 …), one candidate per test, without an invented user story.",
        _ => "",
    };

    // ── Specification ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One candidate per acceptance scenario (Given → precondition, When → step, Then → expected), and one per requirement that no
    /// acceptance scenario names (its statement is the expected behaviour; how to exercise it stays an open question).</summary>
    public static CandidateProviderOutput FromSpecification(string projectKey, CoverageWorkspaceEvidence? workspace)
    {
        if (workspace is null || !workspace.SpecificationAvailable && workspace.AcceptanceScenarios.Count == 0 && workspace.Requirements.Count == 0)
            return new(Capability(Specification, DiscoveryCapabilityState.NoEvidence, 0, "No specification is selected for this project (Specification Explorer).", "Specification parser"), []);
        var module = Text(workspace.ArtifactScope);
        var file = workspace.SpecificationArtifacts.FirstOrDefault()?.Path;
        var list = new List<DiscoveredScenario>();
        var stories = workspace.Stories.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var s in workspace.AcceptanceScenarios.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            var story = s.StoryKey is { } storyKey ? stories.GetValueOrDefault(storyKey) : null;
            var then = Text(Plain(s.Then));
            var when = Text(Plain(s.When));
            list.Add(Finish(new DiscoveredScenario
            {
                CapabilityId = Specification, SourceCategory = "Specification", Area = module, Title = ScenarioTitle(s, when),
                Purpose = then is null ? $"Acceptance scenario {s.Id} of the specification." : $"Confirm that {Lower(then)}",
                Type = ScenarioCandidateType.BusinessFunctional, Confidence = then is null ? CandidateConfidence.Insufficient : CandidateConfidence.ExplicitSpecification,
                ExpectedBehaviour = then, ExpectedBasis = then is null ? null : $"Acceptance scenario {s.Id} (Then)",
                Preconditions = Text(Plain(s.Given)) is { } given ? [given] : [],
                Steps = when is null ? [] : [new DiscoveredStep(when, then)],
                Template =
                [
                    new("given", "Starting situation (Given)", Text(Plain(s.Given))), new("when", "Action (When)", when), new("then", "Expected outcome (Then)", then),
                ],
                RequirementIds = [.. s.RequirementIds.Distinct(StringComparer.OrdinalIgnoreCase)],
                Evidence = [new("specification", $"{file ?? "specification"}#{s.Id}", Clip(s.Title, 200)),
                    .. story is null ? Array.Empty<CandidateEvidenceRef>() : [new CandidateEvidenceRef("story", story.Key, Clip(story.Title, 200))],
                    .. s.RequirementIds.Select(r => new CandidateEvidenceRef("requirement", r))],
                CanonicalKey = $"scenario:{module}|{s.Id}", Engine = "Specification parser (acceptance scenarios)",
                // Positional ids ("scenario-3") change when a scenario is inserted or removed; across imports a scenario is the same scenario when its
                // file, starting situation and action are the same (a changed expected result keeps it; a removed one is gone, never "renamed").
                SourceIdentity = SourceIdentityOf(Specification, $"situation:{SpecFile(file)}|{Situation(s.Given)}|{Situation(s.When)}"),
                OpenQuestions = [.. when is null ? new[] { "The scenario states no action (When): add the steps." } : [], .. then is null ? new[] { "The scenario states no outcome (Then): clarify the expected result." } : []],
            }, projectKey) with { Story = story, NumberInStory = story is null ? null : s.NumberInStory });
            list[^1] = WithClarifications(list[^1], workspace.Clarifications, [.. s.RequirementIds, s.Id, .. story?.StoryId is { } sid ? new[] { sid } : []],
                [s.Given, s.When, s.Then]);
        }
        var named = workspace.AcceptanceScenarios.SelectMany(s => s.RequirementIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in workspace.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.Id) && !named.Contains(r.Id)))
        {
            var statement = Text(RequirementStatement(r.Id, r.Text));
            list.Add(Finish(new DiscoveredScenario
            {
                CapabilityId = Specification, SourceCategory = "Specification", Area = module, Title = $"{r.Id}: {Clip(statement, 90)}", Purpose = statement ?? r.Id,
                Type = ScenarioCandidateType.BusinessFunctional, Confidence = statement is null ? CandidateConfidence.Insufficient : CandidateConfidence.ExplicitSpecification,
                ExpectedBehaviour = statement, ExpectedBasis = statement is null ? null : $"Requirement {r.Id} statement",
                RequirementIds = [r.Id], Evidence = [new("requirement", $"{file ?? "specification"}#{r.Id}", Clip(statement, 200))],
                CanonicalKey = $"requirement:{module}|{r.Id}", Engine = "Specification parser (requirements)",
                OpenQuestions = ["No acceptance scenario describes how to exercise this requirement: add the steps."],
            }, projectKey));
            list[^1] = WithClarifications(list[^1], workspace.Clarifications, [r.Id], [r.Text]);
        }
        var general = workspace.Clarifications.Count(c => c.AffectedIds.Count == 0 && c.State != CoverageClarification.Resolved);
        return new(Capability(Specification, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available, list.Count,
            list.Count == 0 ? "The selected specification has no acceptance scenarios or requirements the parser recognises."
                : $"{workspace.AcceptanceScenarios.Count} acceptance scenarios and {workspace.Requirements.Count} requirements{(module is null ? "" : $" ({module})")}."
                  + (general == 0 ? "" : $" {general} clarification{(general == 1 ? "" : "s")} of the specification name{(general == 1 ? "s" : "")} no requirement: read {(general == 1 ? "it" : "them")} in Specification Review before testing."),
            "Specification parser"), list);
    }

    /// <summary>A specification file path as an identity part: the same file whatever module scope or path spelling the reader used.</summary>
    private static string SpecFile(string? path) => (path ?? "").Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    /// <summary>The suffix that tells two scenarios with the same situation apart: their expected result.</summary>
    public static string TwinSuffix(string? expected) => E.Sha(Situation(expected))[..12];

    private static string Situation(string? text) => string.Join(' ', (Plain(text) ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static readonly System.Text.RegularExpressions.Regex ClarificationMarker =
        new(@"\[\s*NEEDS CLARIFICATION\s*:?\s*([^\]]*)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The specification's clarifications about a candidate (any of its requirement, scenario or story ids named by the clarification) and the
    /// inline [NEEDS CLARIFICATION: …] markers in its own text. An open clarification or marker is an open question (the expected result may
    /// change); a documented answer and a resolution are shown on the case. A candidate without clarifications keeps its fingerprint unchanged.
    /// </summary>
    public static DiscoveredScenario WithClarifications(DiscoveredScenario c, IReadOnlyList<CoverageClarification> clarifications, IReadOnlyList<string> ids,
        IEnumerable<string?> ownText)
    {
        var own = ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var about = clarifications.Where(k => k.AffectedIds.Any(own.Contains)).DistinctBy(k => k.Id).ToList();
        var markers = ownText.Where(t => !string.IsNullOrWhiteSpace(t)).SelectMany(t => ClarificationMarker.Matches(t!).Select(m => m.Groups[1].Value.Trim()))
            .Distinct(StringComparer.Ordinal).ToList();
        if (about.Count == 0 && markers.Count == 0) return c;
        var questions = about.Where(k => k.State == CoverageClarification.Open).Select(k => $"Open clarification in the specification: {Clip(k.Question, 300)}")
            .Concat(markers.Select(m => m.Length == 0 ? "The specification marks this as NEEDS CLARIFICATION." : $"The specification marks this as NEEDS CLARIFICATION: {Clip(m, 300)}"));
        var added = c with { Clarifications = about, OpenQuestions = [.. c.OpenQuestions, .. questions] };
        return added with { Fingerprint = E.Sha(c.Fingerprint + "|" + JsonSerializer.Serialize(new { about, markers }, E.Json)) };
    }

    /// <summary>Plain text of a specification fragment for display: list markers, emphasis and code ticks removed (content is never rewritten).</summary>
    public static string? Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) t = t[2..].TrimStart();
        return t.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
    }

    /// <summary>A requirement's statement without its own id label ("- **FR-001**: The system MUST …" → "The system MUST …").</summary>
    public static string? RequirementStatement(string id, string? text)
    {
        var t = Plain(text);
        if (t is null) return null;
        if (t.StartsWith(id, StringComparison.OrdinalIgnoreCase)) t = t[id.Length..].TrimStart(':', '.', ' ', '-', '–').Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>A readable scenario title: the parser's title when it says more than the starting situation, else "Given — When".</summary>
    private static string ScenarioTitle(BirkNext.TestCoverage.QaTestCandidate s, string? when)
    {
        var given = Text(Plain(s.Given));
        var title = Text(Plain(s.Title));
        if (title is not null && !string.Equals(title, given, StringComparison.OrdinalIgnoreCase) && !string.Equals(title, when, StringComparison.OrdinalIgnoreCase)) return title;
        if (given is not null && when is not null) return $"{Upper(given)} — {when}";
        return Upper(when ?? given ?? title ?? s.Id);
    }

    private static string Upper(string text) => text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    private static string Lower(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    // ── System Understanding guide ───────────────────────────────────────────────────────────────────────────

    /// <summary>The source roles a guide scenario's evidence lies in: its own, its steps', and the rules that name it. Empty = the guide does not say.</summary>
    public static IReadOnlySet<string> ScenarioRoles(SystemGuideContent guide, GuideTestScenario s) =>
        s.Evidence.Concat(s.StepIds.Select(id => guide.Steps.FirstOrDefault(x => x.Id == id)).OfType<GuideStep>().SelectMany(x => x.Evidence))
            .Concat(guide.Rules.Where(r => r.TestScenarioIds.Contains(s.Id, StringComparer.Ordinal)).SelectMany(r => r.Evidence))
            .Select(e => e.SourceRole).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether a guide scenario belongs to a project whose source the guide recognised as <paramref name="recognisedRoles"/>: its evidence names at
    /// least one of them, or names no role at all (the guide does not say where it lies). A cross-system guide recognised through one role (a
    /// Meldingsutveksler archive, "mu") never offers scenarios evidenced only in another system's source ("m2lb"): the project cannot test them.
    /// </summary>
    public static bool InProjectSource(SystemGuideContent guide, GuideTestScenario s, IReadOnlySet<string>? recognisedRoles)
    {
        if (recognisedRoles is null) return true;
        var roles = ScenarioRoles(guide, s);
        return roles.Count == 0 || roles.Overlaps(recognisedRoles);
    }

    public static List<DiscoveredScenario> FromGuide(string projectKey, SystemGuideRevision revision, IReadOnlySet<string>? recognisedRoles = null)
    {
        var guide = revision.Content;
        var reviewed = revision.State == GuideRevisionState.Published;
        var list = new List<DiscoveredScenario>();
        foreach (var s in guide.Scenarios.Where(s => InProjectSource(guide, s, recognisedRoles)))
        {
            var steps = s.StepIds.Select(id => guide.Steps.FirstOrDefault(x => x.Id == id)).OfType<GuideStep>().ToList();
            var components = steps.Select(x => x.ComponentId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            var stage = E.StageOf(guide, s);
            var expected = s.State == GuideScenarioState.NeedsClarification ? null : Text(s.ExpectedOutcome);
            list.Add(Finish(new DiscoveredScenario
            {
                CapabilityId = SystemGuide, SourceCategory = "System Understanding guide", Area = guide.Stages.FirstOrDefault(x => x.Id == stage)?.Title,
                Title = s.Title, Purpose = s.Objective,
                // Cross-system: the guide's own steps run through more than one component.
                Type = components.Count > 1 ? ScenarioCandidateType.BusinessE2E : ScenarioCandidateType.BusinessFunctional,
                Confidence = reviewed ? CandidateConfidence.ExplicitSpecification : CandidateConfidence.CandidateAssociation,
                ExpectedBehaviour = expected, ExpectedBasis = expected is null ? null : $"Guide {revision.GuideKey} revision {revision.Revision} ({(reviewed ? "published" : "not reviewed")})",
                Preconditions = Text(s.Preconditions) is { } pre ? [pre] : [],
                Steps = [.. steps.Select(x => new DiscoveredStep(string.IsNullOrWhiteSpace(x.HowToVerify) ? x.Title : $"{x.Title}: {x.HowToVerify}", Text(x.ExpectedResult)))],
                Template = components.Count > 1
                    ? [new("trigger", "Trigger", steps.FirstOrDefault()?.Title), new("systems", "Systems involved", string.Join(" → ", components.Select(id => guide.Components.FirstOrDefault(c => c.Id == id)?.Name ?? id))),
                       new("outcome", "Business outcome", expected)]
                    : [],
                TestDataNeeds = Text(s.TestData) is { } data ? [data] : [],
                RequirementIds = [.. guide.Rules.Where(r => r.TestScenarioIds.Contains(s.Id, StringComparer.Ordinal)).Select(r => r.Id)],
                Evidence = [new("guide-scenario", $"{revision.GuideKey}/{s.Id}", $"Guide revision {revision.Revision}"),
                    .. s.Evidence
                    .Concat(steps.SelectMany(step => step.Evidence))
                    .Concat(guide.Rules.Where(rule => rule.TestScenarioIds.Contains(s.Id, StringComparer.Ordinal)).SelectMany(rule => rule.Evidence))
                    .Distinct()
                    .Select(e => new CandidateEvidenceRef("source-evidence",
                        string.Join(":", new[] { e.SourceLabel, e.File, e.Lines is null ? null : $"lines {e.Lines}" }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        string.Join(" · ", new[] { e.SourceLabel, e.Class.ToString(), e.Symbol, e.TestId, e.Note }.Where(x => !string.IsNullOrWhiteSpace(x))))),
                    .. s.SourceIdentity is { Length: > 0 } derived ? new[] { new CandidateEvidenceRef(ScenarioConsolidation.DerivedFrom, derived) } : []],
                CanonicalKey = $"guide:{revision.GuideKey}/{s.Id}",
                // A scenario generated from a discovered one names it: consolidation merges the two (the guide adds its review), never offering both.
                ScopeKeys = ["Scenario:" + BirkNext.Interpretation.CanonicalKeys.EntityKey(BirkNext.Interpretation.CanonicalEntityType.Scenario,
                    BirkNext.Interpretation.CorrelationRules.GuideScenarioIdentity(revision.GuideKey, s.Id))],
                Engine = $"System Understanding guide r{revision.Revision}",
                OpenQuestions = [.. s.State == GuideScenarioState.NeedsClarification ? new[] { "The guide marks this scenario as needing clarification." } : [],
                    .. Text(s.FollowUp) is { } followUp ? new[] { followUp } : []],
            }, projectKey));
        }
        if (recognisedRoles is { Count: > 0 }) list.AddRange(FromGuideSteps(projectKey, revision, recognisedRoles, list));
        return list;
    }

    /// <summary>
    /// The guide's steps that lie in this project's own source (their evidence names a recognised role) and that no offered scenario covers, as
    /// step checks: a project that is one part of a cross-system guide (MU of Skolenærvær) still gets what the guide says about its own part —
    /// what happens, the expected result and what can go wrong. A step whose evidence names no role is never assumed to be this project's.
    /// </summary>
    public static List<DiscoveredScenario> FromGuideSteps(string projectKey, SystemGuideRevision revision, IReadOnlySet<string> recognisedRoles, IReadOnlyList<DiscoveredScenario> offered)
    {
        var guide = revision.Content;
        var reviewed = revision.State == GuideRevisionState.Published;
        var offeredIds = offered.Where(c => c.CapabilityId == SystemGuide).SelectMany(c => c.Evidence).Select(e => e.Reference.Split('/').Last()).ToHashSet(StringComparer.Ordinal);
        var covered = guide.Scenarios.Where(s => offeredIds.Contains(s.Id)).SelectMany(s => s.StepIds).ToHashSet(StringComparer.Ordinal);
        var list = new List<DiscoveredScenario>();
        foreach (var step in guide.Steps.Where(x => !covered.Contains(x.Id)
                     && x.Evidence.Any(e => e.SourceRole is { } role && recognisedRoles.Contains(role.Trim()))))
        {
            // The step's expected result, else what the guide says it produces (its output) — never invented.
            var fromOutput = Text(step.ExpectedResult) is null && Text(step.Output) is not null;
            var expected = Text(step.ExpectedResult) ?? (Text(step.Output) is { } output ? $"Produces: {output}" : null);
            var component = guide.Components.FirstOrDefault(c => c.Id == step.ComponentId)?.Name;
            var watch = step.WhatCanGoWrong.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).ToList();
            list.Add(Finish(new DiscoveredScenario
            {
                CapabilityId = SystemGuide, SourceCategory = "System Understanding guide", Area = guide.Stages.FirstOrDefault(x => x.Id == step.Stage)?.Title ?? component,
                Title = step.Title, Purpose = Text(step.Summary) ?? step.Title,
                Type = ScenarioCandidateType.BusinessFunctional,
                Confidence = reviewed ? CandidateConfidence.ExplicitSpecification : CandidateConfidence.CandidateAssociation,
                ExpectedBehaviour = expected, ExpectedBasis = expected is null ? null : $"Guide {revision.GuideKey} revision {revision.Revision}, step {step.Id}{(fromOutput ? " output" : "")} ({(reviewed ? "published" : "not reviewed")})",
                Preconditions = Text(step.Trigger) is { } trigger ? [$"Trigger: {trigger}"] : [],
                Steps = [new DiscoveredStep(string.IsNullOrWhiteSpace(step.HowToVerify) ? step.Title : $"{step.Title}: {step.HowToVerify}", expected),
                    .. watch.Select(w => new DiscoveredStep($"Check what happens when: {w}", "Handled as the guide describes; nothing is lost or silently accepted"))],
                TestDataNeeds = Text(step.Input) is { } input ? [input] : [],
                Evidence = [new("guide-step", $"{revision.GuideKey}/step/{step.Id}", $"Guide revision {revision.Revision}")],
                CanonicalKey = $"guide:{revision.GuideKey}/step/{step.Id}",
                Engine = $"System Understanding guide r{revision.Revision}",
                OpenQuestions = ["The guide describes this step but has no test scenario for it yet: review the expected result before testing."],
            }, projectKey));
        }
        return list;
    }

    private static readonly string[] FrameworkRoutes = ["/error", "/not-found", "/notfound", "/404", "/500", "/access-denied", "/accessdenied", "/logout", "/signout"];
    private static readonly string[] DeveloperToolSegments = ["demo", "debug", "debugpanel", "sample", "samples", "example", "examples", "playground", "sandbox", "devtools", "storybook"];

    /// <summary>A framework page every web app has (error, not found, the sign-in callback) — not an application function a tester checks.</summary>
    public static bool IsFrameworkPage(SourceUiPage page) =>
        page.Routes.Count > 0 && page.Routes.All(r => FrameworkRoutes.Contains(r.TrimEnd('/').ToLowerInvariant(), StringComparer.Ordinal)
            || r.StartsWith("/authentication/", StringComparison.OrdinalIgnoreCase) || r.StartsWith("/signin-", StringComparison.OrdinalIgnoreCase));

    /// <summary>A page of a developer or demo tool in the repository (a project or folder named Demo, Debug, Sample, Example, Playground …).</summary>
    public static bool IsDeveloperToolPage(SourceUiPage page) =>
        $"{page.Module}/{page.File}".Split(['/', '\\', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => DeveloperToolSegments.Contains(segment.ToLowerInvariant(), StringComparer.Ordinal));

    // ── UI source (Critical E2E suggestion engine) ──────────────────────────────────────────────────────────

    /// <summary>Suggestions of the existing frontend UI analyzer, as manual candidates. A page that only opens is UI smoke, never business E2E.</summary>
    public static List<DiscoveredScenario> FromUi(string projectKey, IEnumerable<TestSuggestion> suggestions) => [.. suggestions.Select(s =>
    {
        var type = s.Category == SuggestionCategory.UiFunctional ? ScenarioCandidateType.UiFunctional
            : s.Category == SuggestionCategory.BusinessE2E && s.Kind is not (SuggestionKind.PageOpens or SuggestionKind.Navigation) ? ScenarioCandidateType.BusinessE2E
            : ScenarioCandidateType.UiSmoke;
        var actions = s.Steps.Where(x => x.Action != CompanionActionKind.Navigate).Select(x => x.Description).ToList();
        var validation = s.Kind is SuggestionKind.RequiredFields or SuggestionKind.InvalidInput
            ? string.Join("; ", s.Steps.Select(x => x.Expected).Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;
        return Finish(new DiscoveredScenario
        {
            CapabilityId = UiSource, SourceCategory = "UI source", Area = Text(s.Feature) ?? Text(s.Module) ?? s.Route, Title = s.Title,
            Purpose = PurposeOf(s.Kind, s.Feature, s.Route), Type = type, Confidence = CandidateConfidence.SourceDerived,
            ExpectedBehaviour = Text(s.ExpectedResult), ExpectedBasis = Text(s.ExpectedBasis),
            Preconditions = [.. s.Preconditions], Prerequisites = [.. s.Prerequisites],
            Steps = [.. s.Steps.Select(x => new DiscoveredStep(x.Description, Text(x.Expected)))],
            Template =
            [
                new("route", "Page", s.Route), new("action", "User action", actions.Count == 0 ? null : string.Join("; ", actions)),
                new("input", "Input", s.TestDataNeeds.Count == 0 ? null : string.Join("; ", s.TestDataNeeds)),
                new("validation", "Validation", string.IsNullOrWhiteSpace(validation) ? null : validation),
                new("expected-ui", "Expected UI state", Text(s.ExpectedResult)),
            ],
            TestDataNeeds = [.. s.TestDataNeeds], RequirementIds = [.. s.RequirementRefs],
            Evidence = [.. s.SourceRefs.Take(8).Select(r => new CandidateEvidenceRef("source-file", r)), .. s.RequirementRefs.Select(r => new CandidateEvidenceRef("requirement", r))],
            CanonicalKey = "ui:" + s.SuggestionId, Engine = $"UI suggestion engine {s.RuleVersion}",
            // The suggestion id includes the project key, so the same page check had another identity in every import of the application. The
            // identity is the page, the kind of check and its title (2026-11-23); the former one stays findable for cases prepared before.
            SourceIdentity = SourceIdentityOf(UiSource, $"ui-check:{s.Route.Trim().ToLowerInvariant()}|{s.Kind}|{Situation(s.Title)}"),
            FormerIdentities = [SourceIdentityOf(UiSource, "ui:" + s.SuggestionId)],
            OpenQuestions = [.. Text(s.ExpectedHint) is { } hint ? new[] { $"Proposed from source, not confirmed: \"{Clip(hint, 200)}\" — confirm or clarify the expected result." } : []],
        }, projectKey);
    })];

    private static string PurposeOf(SuggestionKind kind, string feature, string route) => kind switch
    {
        SuggestionKind.PageOpens => $"Check that the {feature} page ({route}) opens.",
        SuggestionKind.Navigation => $"Check navigation from the {feature} page.",
        SuggestionKind.RequiredFields => $"Check that the {feature} form refuses missing required fields.",
        SuggestionKind.InvalidInput => $"Check that the {feature} form refuses invalid input.",
        SuggestionKind.ValidSubmission => $"Check that the {feature} form accepts valid input.",
        SuggestionKind.Search or SuggestionKind.SearchNoMatch or SuggestionKind.SearchEmpty or SuggestionKind.ClearSearch or SuggestionKind.SearchOpensResult => $"Check search on the {feature} page.",
        SuggestionKind.FilterList => $"Check filtering on the {feature} page.",
        _ => $"Check a user action on the {feature} page.",
    };

    // ── API / contract ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One candidate per declared operation of an OpenAPI / GraphQL contract (expected response only as documented), and a
    /// security/authorization candidate when the operation declares security and documents a 401/403 response.</summary>
    public static List<DiscoveredScenario> FromContracts(string projectKey, IEnumerable<SourceContract> contracts)
    {
        var list = new List<DiscoveredScenario>();
        foreach (var c in contracts.Where(c => c.Type is SourceContractType.OpenApi or SourceContractType.GraphQlSchema or SourceContractType.GraphQlOperations))
            foreach (var op in c.Operations)
            {
                var required = op.ParameterDetails.Where(p => p.Required).Select(p => $"{p.Name} ({p.In}{(p.Type is null ? "" : ", " + p.Type)})").ToList();
                if (required.Count == 0 && op.ParameterDetails.Count == 0) required = [.. op.Parameters];
                var input = string.Join("; ", required.Concat(op.RequestSchema is { } body ? [$"{(op.RequestBodyRequired ? "required" : "optional")} body {body}"] : []));
                var success = op.ResponseCodes.FirstOrDefault(code => code.StartsWith('2'));
                var expected = success is not null
                    ? $"Responds {success}{(op.ResponseSchemas.TryGetValue(success, out var schema) ? $" with {schema}" : "")}, as the contract documents."
                    : op.ResultType is { } result ? $"Returns {result}, as the schema declares." : null;
                var errors = op.ResponseCodes.Where(code => code.Length == 3 && code[0] == '4').ToList();
                var errorSteps = new List<DiscoveredStep>();
                var validationCode = errors.FirstOrDefault(code => code is "400" or "422");
                if (validationCode is not null && (op.RequestBodyRequired || op.ParameterDetails.Any(p => p.Required)))
                    errorSteps.Add(new($"Send {op.Name} without a required input", $"Responds {validationCode}, as the contract documents."));
                if (errors.Contains("404") && op.ParameterDetails.Any(p => p.In == "path"))
                    errorSteps.Add(new($"Send {op.Name} for an item that does not exist", "Responds 404, as the contract documents."));
                var area = Text(c.Name) ?? c.File;
                var basis = success is not null || op.ResultType is not null ? $"Documented in {c.File}" : null;
                list.Add(Finish(new DiscoveredScenario
                {
                    CapabilityId = ApiContract, SourceCategory = "API contract", Area = area,
                    Title = op.Summary is { Length: > 0 } summary ? $"{op.Name} — {summary}" : op.Name,
                    Purpose = $"Verify {op.Name} as the {area} contract documents it.", Type = ScenarioCandidateType.ApiContract,
                    Confidence = expected is null ? CandidateConfidence.Insufficient : CandidateConfidence.ContractDerived, ExpectedBehaviour = expected, ExpectedBasis = basis,
                    Steps = [new DiscoveredStep(input.Length == 0 ? $"Send {op.Name}" : $"Send {op.Name} with {input}", expected), .. errorSteps],
                    Template =
                    [
                        new("operation", "Operation", op.Name), new("input", "Input", input.Length == 0 ? null : input), new("expected-response", "Expected response", expected),
                        new("schema", "Schema validation", success is not null && op.ResponseSchemas.TryGetValue(success, out var s2) ? $"Response body matches {s2}" : op.ResultType),
                        new("errors", "Error scenarios", errors.Count == 0 ? null : "Documented: " + string.Join(", ", errors)),
                    ],
                    TestDataNeeds = input.Length == 0 ? [] : [$"Valid input for {op.Name}: {input}"],
                    Evidence = [new("contract-operation", $"{c.File}:{c.Line}#{op.Name}", $"{c.Type} {c.Name} {c.Version}".Trim())],
                    CanonicalKey = $"contract-op:{c.File}#{op.Name}", Engine = "Contract analyzer",
                    OpenQuestions = [.. op.Deprecated ? new[] { "The contract marks this operation as deprecated." } : [], .. !op.Detailed ? new[] { "Older snapshot: only parameter names are known; re-analyze the source for request and response details." } : []],
                }, projectKey));
                var denied = errors.FirstOrDefault(code => code is "401" or "403");
                if (op.Security.Count > 0)
                    list.Add(Finish(new DiscoveredScenario
                    {
                        CapabilityId = ApiContract, SourceCategory = "API contract", Area = area, Title = $"{op.Name} — refused without valid authorization",
                        Purpose = $"Verify that {op.Name} requires {string.Join(", ", op.Security)} as the contract declares.", Type = ScenarioCandidateType.SecurityAuthorization,
                        Confidence = denied is null ? CandidateConfidence.Insufficient : CandidateConfidence.ContractDerived,
                        ExpectedBehaviour = denied is null ? null : $"Responds {denied}, as the contract documents.", ExpectedBasis = denied is null ? null : $"Documented in {c.File}",
                        Steps = [new DiscoveredStep($"Send {op.Name} without credentials", denied is null ? null : $"Responds {denied}.")],
                        Template = [new("operation", "Operation", op.Name), new("security", "Declared security", string.Join(", ", op.Security)), new("expected-response", "Expected response", denied is null ? null : $"{denied}")],
                        Evidence = [new("contract-operation", $"{c.File}:{c.Line}#{op.Name}", "security: " + string.Join(", ", op.Security))],
                        CanonicalKey = $"contract-auth:{c.File}#{op.Name}", Engine = "Contract analyzer",
                        OpenQuestions = [.. denied is null ? new[] { "The contract declares security but documents no 401/403 response: clarify the expected refusal." } : []],
                    }, projectKey));
            }
        return list;
    }

    // ── Integration journeys ─────────────────────────────────────────────────────────────────────────────────

    public static List<DiscoveredScenario> FromDeclaredJourneys(string projectKey, IEnumerable<DeclaredIntegrationJourney> journeys) => [.. journeys.Where(j => j.Components.Count >= 2).Select(j =>
    {
        var id = $"{j.PackId}:{j.JourneyId}";
        return Finish(new DiscoveredScenario
        {
            CapabilityId = IntegrationJourneys, SourceCategory = "Integration journey", Area = j.DisplayName, Title = j.DisplayName,
            Purpose = Text(j.Description) ?? $"Follow one message or request through {string.Join(" → ", j.Components)}.",
            Type = ScenarioCandidateType.Integration, Confidence = CandidateConfidence.CandidateAssociation,
            Steps = [.. j.Components.Zip(j.Components.Skip(1)).Select(p => new DiscoveredStep($"{p.First} hands over to {p.Second}", null))],
            Template =
            [
                new("trigger", "Trigger", null), new("producer", "Producer", j.Components[0]), new("consumer", "Consumer", j.Components[^1]),
                new("message", "Message", null), new("processing", "Expected processing", null),
            ],
            Evidence = [new("journey", id, $"Declared by {j.DeclaredBy}, revision {j.Revision}")],
            CanonicalKey = "journey:" + id, ScopeKeys = ["Journey:" + id], Engine = "Declared integration journeys",
            OpenQuestions = ["The declared journey names components only: describe the trigger, the message and the expected processing."],
        }, projectKey);
    })];

    /// <summary>
    /// Test-pack journey scenarios. The pack is the evidence: its stated outcome is the expected behaviour (with the pack as basis), and what it
    /// says is still open is classified (a missing test prerequisite or observation is not an unclear expectation). The pack is associated
    /// with the project through its recognised source roles only, so the association stays a candidate for review.
    /// </summary>
    public static List<DiscoveredScenario> FromPackJourneys(string projectKey, IIntegrationJourneyPack pack, IReadOnlySet<string>? journeyIds = null) =>
        [.. pack.Journeys.Where(j => journeyIds is null || journeyIds.Contains(j.JourneyId)).SelectMany(j => j.Scenarios.Select(s =>
    {
        var id = $"{pack.PackId}:{j.JourneyId}:{s.ScenarioId}";
        var first = j.Steps.FirstOrDefault();
        var last = j.Steps.LastOrDefault();
        var packName = $"{pack.DisplayName} {pack.PackVersion}";
        var gaps = PackGaps(s, packName);
        return Finish(new DiscoveredScenario
        {
            CapabilityId = IntegrationJourneys, SourceCategory = "Integration journey", Area = j.DisplayName, Title = $"{j.DisplayName} · {s.DisplayName}",
            Purpose = Text(s.Description) ?? j.Description, Type = ScenarioCandidateType.Integration, Confidence = CandidateConfidence.CandidateAssociation,
            ExpectedBehaviour = Text(s.ExpectedOutcome), ExpectedBasis = s.ExpectedOutcome is null ? null : $"Stated by the test pack {packName}",
            Steps = [.. j.Steps.Select(x => new DiscoveredStep(x.Label, Text(x.ExpectedEvidence)))],
            Template =
            [
                new("trigger", "Trigger", first?.Label), new("producer", "Producer", first?.FromComponent ?? first?.Owner), new("consumer", "Consumer", last?.ToComponent ?? last?.Owner),
                new("message", "Message", null), new("processing", "Expected processing", Text(last?.ExpectedEvidence)),
            ],
            Evidence = [new("journey", id, packName)],
            CanonicalKey = "journey:" + id, Engine = $"Test pack {pack.PackId} {pack.PackVersion}",
            Gaps = gaps,
            OpenQuestions = [.. gaps.Where(g => g.Kind != EvidenceGapKind.AssociationNeedsReview).Select(g => $"{EvidenceGaps.Label(g.Kind)}: {g.Question}")],
            SourcesChecked = [new($"Test pack {packName}", true, s.Gaps is { Count: > 0 } || s.ExpectedOutcome is not null
                ? "Scenario, steps, stated outcome and open points"
                : "Scenario and steps; the pack states no separate outcome")],
        }, projectKey);
    }))];

    /// <summary>The pack's classified open points; a pack that only gives a free-text reason keeps it as one unclassified point (never guessed).</summary>
    internal static List<EvidenceGap> PackGaps(JourneyScenarioDescriptor s, string packName)
    {
        var gaps = new List<EvidenceGap>();
        if (s.ExpectedOutcome is null && (s.Gaps is null || s.Gaps.All(g => g.Kind != EvidenceGapKind.BusinessExpectationUnclear)))
            gaps.Add(new(EvidenceGapKind.BusinessExpectationUnclear, "The test pack describes the scenario but states no expected outcome separately from how it is run.",
                $"Test pack {packName}", "Define the expected result when preparing the test case."));
        if (s.Gaps is { Count: > 0 } declared) gaps.AddRange(declared);
        else if (s.Support != JourneyScenarioSupport.Supported)
            gaps.Add(new(EvidenceGapKind.EvidenceNotEvaluated, $"Not assessed in the pack: {WithoutNotAssessedPrefix(s.NotAssessedBecause) ?? "no reason given"}.", $"Test pack {packName}"));
        gaps.Add(new(EvidenceGapKind.AssociationNeedsReview,
            "The test pack is associated with this project because it recognises the project's source roles; nobody has confirmed the association.", "Source role recognition"));
        return gaps;
    }

    /// <summary>"Not assessed: …" / "Not assessed — …" reasons are shown under a heading that already says so: drop the repeated prefix.</summary>
    internal static string? WithoutNotAssessedPrefix(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var text = reason.Trim();
        foreach (var prefix in new[] { "Not assessed:", "Not assessed —", "Not assessed -", "Not verified:" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return text[prefix.Length..].Trim().TrimEnd('.');
        return text.TrimEnd('.');
    }

    /// <summary>
    /// Adds what the pack's run history in the active Target Environment shows: the last run of each scenario as evidence. Only a Completed
    /// run removes "Runtime evidence missing"; any other state is shown as it is. Without an environment the history is reported as not
    /// examined rather than as missing evidence. One history read per pack (no per-row query).
    /// </summary>
    public static List<DiscoveredScenario> WithRunEvidence(List<DiscoveredScenario> scenarios, IIntegrationJourneyPack pack, string? environmentId, string? environmentName,
        IReadOnlyList<IntegrationJourneyRun>? runs) => [.. scenarios.Select(c =>
    {
        var packId = pack.PackId;
        if (!c.CanonicalKey.StartsWith($"journey:{packId}:", StringComparison.Ordinal)) return c;
        var key = c.CanonicalKey["journey:".Length..];
        // A scenario the pack cannot run yet is not "missing a run": its own gaps say what must exist first.
        var runnable = pack.Journeys.SelectMany(j => j.Scenarios.Select(s => (Key: $"{packId}:{j.JourneyId}:{s.ScenarioId}", s.Support)))
            .Any(x => x.Key == key && x.Support == JourneyScenarioSupport.Supported);
        if (environmentId is null || runs is null)
            return c with
            {
                SourcesChecked = [.. c.SourcesChecked, new("Integration journey runs", false, "Not examined: no active Target Environment")],
                Gaps = [.. c.Gaps, new(EvidenceGapKind.EvidenceNotEvaluated, "Run history was not examined because no Target Environment is active.", "Integration journey runs",
                    "Choose a Target Environment, then discover again.")],
            };
        var where = string.IsNullOrWhiteSpace(environmentName) ? "the active Target Environment" : environmentName;
        var latest = runs.Where(r => $"{r.PackId}:{r.JourneyId}:{r.ScenarioId}" == key).OrderByDescending(r => r.StartedAt).FirstOrDefault();
        if (latest is null)
            return c with
            {
                SourcesChecked = [.. c.SourcesChecked, new("Integration journey runs", false, $"No run in {where}")],
                Gaps = !runnable || c.Gaps.Any(g => g.Kind == EvidenceGapKind.RuntimeEvidenceMissing) ? c.Gaps
                    : [.. c.Gaps, new(EvidenceGapKind.RuntimeEvidenceMissing, $"The scenario has not been run in {where}.", "Integration journey runs", "Run it from Integration Testing.")],
            };
        var verified = latest.OverallState == JourneyRunState.Completed;
        var when = latest.StartedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return c with
        {
            Evidence = [.. c.Evidence, new("journey-run", latest.RunId.ToString(), $"{latest.OverallState} in {where}, {when} UTC")],
            SourcesChecked = [.. c.SourcesChecked, new("Integration journey runs", true, $"Last run {latest.OverallState} ({when} UTC)")],
            Gaps = verified
                ? [.. c.Gaps.Where(g => g.Kind != EvidenceGapKind.RuntimeEvidenceMissing)]
                : [.. c.Gaps.Where(g => g.Kind != EvidenceGapKind.RuntimeEvidenceMissing),
                    new(EvidenceGapKind.RuntimeEvidenceMissing, $"The last run in {where} ended {latest.OverallState}: {Text(latest.StateReason) ?? "no reason recorded"}.",
                        "Integration journey runs", "Open the run in Integration Testing.")],
        };
    })];

    // ── Existing tests ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Explicit requirement references of discovered tests (Confirmed/StronglySupported link; Inferred ones are candidates needing review).</summary>
    public static List<RequirementTestLink> TestLinks(SourceTestInventory inventory) => [.. inventory.Definitions.SelectMany(d => d.References
        .Where(r => r.Kind == TestReferenceKind.Requirement && r.Confidence != TestEvidenceConfidence.Unresolved)
        .Select(r => new RequirementTestLink(r.Id, Association(d, $"The test references {r.Id} ({r.Basis})",
            r.Confidence is TestEvidenceConfidence.Confirmed or TestEvidenceConfidence.StronglySupported, TestScenarioLinkOrigin.SharedRequirement))))];

    /// <summary>The acceptance scenarios discovered tests name (2026-11-25), each scoped to the test's module.</summary>
    public static List<ScenarioTestLink> ScenarioLinks(SourceTestInventory inventory) => [.. inventory.Definitions.SelectMany(d => d.Scenarios
        .Where(r => r.Confidence != TestEvidenceConfidence.Unresolved)
        .Select(r => new ScenarioTestLink(ModuleOf(d.FilePath), r.StoryId, r.Number, Association(d, r.Basis,
            r.Confidence is TestEvidenceConfidence.Confirmed or TestEvidenceConfidence.StronglySupported, TestScenarioLinkOrigin.Scenario))))];

    private static CandidateTestAssociation Association(SourceTestDefinition d, string basis, bool confirmed, TestScenarioLinkOrigin origin) =>
        new(d.TestDefinitionId, d.DisplayName ?? d.MethodName, $"{d.FilePath}:{d.Line}", basis, confirmed)
        {
            Origin = origin, Layer = d.Kind.ToString(), Framework = d.Framework, ClassName = d.ClassName, MethodName = d.MethodName, SourceFingerprint = d.SourceFingerprint,
        };

    /// <summary>The module a path belongs to: its first folder ("SkoleService/tests/…" → "SkoleService"); "" for a file at the root.</summary>
    public static string ModuleOf(string? path)
    {
        var p = (path ?? "").Replace('\\', '/').TrimStart('/');
        var slash = p.IndexOf('/');
        return slash > 0 ? p[..slash] : "";
    }

    // ── Finalisation ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deduplicates on the canonical identity only (same canonical key → one candidate, evidence unioned), attaches existing tests by explicit
    /// requirement id, and notes "possible duplicate" between candidates of different sources that name the same requirement — never merged.
    /// Order: by source category, area, title.
    /// </summary>
    /// <summary>
    /// Cross-page context hardening (2026-11-26): a specification scenario's key was «scenario:{module}|{id}», so US1/AC1 of two specifications of the
    /// SAME module had one key and one candidate id — merged into one candidate here, and «Forbered testtilfelle» for one could open the other's
    /// case. When one key is claimed by more than one specification file, every claimant gets a specification-scoped key («…|{file}|{id}») and so
    /// its own candidate id. Keys without a collision keep their id exactly (existing plans keep their links); source identities, already
    /// file-scoped, never change.
    /// </summary>
    internal static IEnumerable<DiscoveredScenario> ScopeCollidingSpecificationKeys(string projectKey, IEnumerable<DiscoveredScenario> candidates)
    {
        var list = candidates.ToList();
        static string? FileOf(DiscoveredScenario c) => c.Story?.SpecificationPath is { Length: > 0 } spec ? spec : c.Evidence.FirstOrDefault(e => e.Kind is "specification" or "requirement")?.Reference.Split('#')[0];
        var colliding = list.Where(c => c.CapabilityId == Specification && (c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal) || c.CanonicalKey.StartsWith("requirement:", StringComparison.Ordinal)))
            .GroupBy(c => c.CanonicalKey, StringComparer.Ordinal)
            // A scenario's source identity is file- and situation-scoped (the same scenario seen twice keeps it); a requirement's derives from the key,
            // so its specification file decides.
            .Where(g => (g.Key.StartsWith("scenario:", StringComparison.Ordinal)
                ? g.Select(c => c.SourceIdentity).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Count()
                : g.Select(FileOf).Distinct(StringComparer.OrdinalIgnoreCase).Count()) > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (colliding.Count == 0) return list;
        return list.Select(c =>
        {
            if (!colliding.Contains(c.CanonicalKey) || FileOf(c) is not { Length: > 0 } file) return c;
            var bar = c.CanonicalKey.IndexOf('|');
            var key = bar < 0 ? $"{c.CanonicalKey}|{file}" : $"{c.CanonicalKey[..bar]}|{file}|{c.CanonicalKey[(bar + 1)..]}";
            return c with { CanonicalKey = key, CandidateId = CandidateId(projectKey, c.CapabilityId, key) };
        });
    }

    /// <summary>
    /// Which candidates the cap (<see cref="MaxCandidates"/>) keeps first (2026-11-26): before, it kept the first ones in provider order, so what
    /// was dropped depended on the order providers ran. Now the source's own scenarios (specification ACs, documented TEST-E tests) come first,
    /// then guide and journey flows, then the derived technical ones. Stable within a rank (OrderBy is stable), so the order inside a
    /// capability is unchanged.
    /// </summary>
    public static int CapPriority(string capabilityId) => capabilityId switch
    {
        Specification => 0, TestSpecification => 1, SystemGuide => 2, IntegrationJourneys => 3, UiSource => 4, ApiContract => 5, ExistingTests => 6, DeveloperTests => 7,
        _ => 8,
    };

    /// <summary>What the cap dropped, per capability (for the limitation text): the merged candidates beyond <see cref="MaxCandidates"/>.</summary>
    public static IReadOnlyDictionary<string, int> DroppedByCap(string projectKey, IEnumerable<DiscoveredScenario> candidates)
    {
        var all = candidates.Where(c => c.ProjectKey == projectKey).DistinctBy(c => c.CandidateId).OrderBy(c => CapPriority(c.CapabilityId)).ToList();
        return all.Skip(MaxCandidates).GroupBy(c => c.CapabilityId).ToDictionary(g => g.Key, g => g.Count());
    }

    public static List<DiscoveredScenario> Finalize(string projectKey, string snapshotKey, IEnumerable<DiscoveredScenario> candidates, IEnumerable<RequirementTestLink> links,
        IEnumerable<ScenarioTestLink>? scenarioLinks = null)
    {
        var merged = ScopeCollidingSpecificationKeys(projectKey, candidates.Where(c => c.ProjectKey == projectKey)).GroupBy(c => c.CandidateId, StringComparer.Ordinal).Select(g => g.Skip(1).Aggregate(g.First(), (a, b) => a with
        {
            Evidence = [.. a.Evidence.Concat(b.Evidence).DistinctBy(e => (e.Kind, e.Reference))],
            RequirementIds = [.. a.RequirementIds.Concat(b.RequirementIds).Distinct(StringComparer.OrdinalIgnoreCase)],
            ScopeKeys = [.. a.ScopeKeys.Concat(b.ScopeKeys).Distinct(StringComparer.Ordinal)],
        })).OrderBy(c => CapPriority(c.CapabilityId)).Take(MaxCandidates).Select(c => c with { SnapshotKey = c.SnapshotKey.Length > 0 ? c.SnapshotKey : snapshotKey }).ToList();
        // Two scenarios of one file with the same situation (different outcomes) keep distinct identities, in their order.
        // They are told apart by their expected result (never by position, which shifts when one is removed); only identical twins get an ordinal.
        merged = [.. merged.GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).SelectMany(g => g.Count() == 1 || g.Key.Length == 0 ? g
            : g.Select(c => c with { SourceIdentity = $"{c.SourceIdentity}|{TwinSuffix(c.ExpectedBehaviour)}" })
               .GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).SelectMany(t => t.Select((c, i) => i == 0 ? c : c with { SourceIdentity = $"{c.SourceIdentity}#{i + 1}" })))];
        // Cross-source consolidation: exact duplicates merged (evidence kept), equivalent/related/variant noted, conflicting expected results recorded.
        merged = ScenarioConsolidation.Apply(merged);
        var byRequirement = links.GroupBy(l => Norm(l.RequirementId)).ToDictionary(g => g.Key, g => g.Select(l => l.Test).ToList(), StringComparer.Ordinal);
        // Scenario links (2026-11-25): a test naming «US5 scenario 9» belongs to the scenario numbered 9 under US5 in a specification of the test's own
        // module. When that (module, story, number) exists in more than one specification, every match stays a candidate (never chosen by guesswork).
        static string? ScenarioKey(DiscoveredScenario c) => c.Story is { StoryId: { Length: > 0 } sid, SpecificationPath: var spec } && c.NumberInStory is { } n
            ? $"{ModuleOf(spec)}|{sid.ToUpperInvariant()}|{n}" : null;
        var scenarioHolders = merged.Select(c => ScenarioKey(c)).OfType<string>().GroupBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var byScenario = (scenarioLinks ?? []).GroupBy(l => $"{l.Module}|{l.StoryId.ToUpperInvariant()}|{l.Number}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Test).ToList(), StringComparer.OrdinalIgnoreCase);
        var sharing = merged.SelectMany(c => c.RequirementIds.Select(r => (Req: Norm(r), c.CandidateId, c.CapabilityId, Module: SpecModule(c)))).GroupBy(x => x.Req).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // Requirement ids are local to a specification (v1.0 acceptance 2026-11-25): in a repository of several modules, FR-025 of SkoleService is not
        // FR-025 of PersonService. A scenario whose specification is <M>/specs/… is given only requirement-based tests of module M when M has tests of
        // its own and tests exist in at least two modules (decided per scenario: a discovery often covers one module only). A specification at the root,
        // or a module without its own tests (e.g. MyApp/specs + MyApp.Tests), keeps the project-wide match. Possible duplicates never cross two
        // different <M>/specs modules.
        var testModules = links.Select(l => ModuleOf(l.Test.File)).Where(m => m.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ScopedTo(string? m) => m is not null && testModules.Count >= 2 && testModules.Contains(m);
        static bool SameModule(string? scenarioModule, string? otherModule) =>
            scenarioModule is null || otherModule is null || string.Equals(scenarioModule, otherModule, StringComparison.OrdinalIgnoreCase);
        return [.. merged.Select(c =>
        {
            var key = ScenarioKey(c);
            var module = SpecModule(c);
            var moduleScoped = ScopedTo(module);
            var named = key is not null && byScenario.TryGetValue(key, out var found) ? found : [];
            if (key is not null && scenarioHolders.GetValueOrDefault(key) > 1)
                named = [.. named.Select(t => t with { Confirmed = false, Basis = t.Basis + " — the module has this story and scenario number in more than one specification" })];
            var tests = named.Concat(c.RequirementIds.SelectMany(r => byRequirement.GetValueOrDefault(Norm(r)) ?? [])
                .Where(t => !moduleScoped || string.Equals(ModuleOf(t.File), module, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(t => t.TestId).Take(20).ToList();
            var duplicates = c.RequirementIds.SelectMany(r => sharing.GetValueOrDefault(Norm(r)) ?? [])
                .Where(x => x.CandidateId != c.CandidateId && x.CapabilityId != c.CapabilityId && SameModule(module, x.Module))
                .Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Take(5).ToList();
            return c with { ExistingTests = tests, PossibleDuplicates = duplicates };
        }).OrderBy(c => c.SourceCategory, StringComparer.Ordinal).ThenBy(c => c.Area ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string Norm(string requirementId) => requirementId.Trim().ToUpperInvariant();

    /// <summary>The module of a scenario's specification when the repository is laid out by module ("SkoleService/specs/001-…/spec.md" →
    /// "SkoleService"): from its user story, else from its requirement evidence ("SkoleService/specs/001-…/spec.md#FR-025" — a scenario derived from
    /// a requirement has no story). Null for a specification at the root ("specs/001/spec.md") or a scenario without one.</summary>
    public static string? SpecModule(DiscoveredScenario c) =>
        PathModule(c.Story?.SpecificationPath) ?? c.Evidence.Select(e => PathModule(e.Reference.Split('#')[0])).FirstOrDefault(m => m is not null);

    private static string? PathModule(string? specificationPath)
    {
        var parts = (specificationPath ?? "").Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[1].Equals("specs", StringComparison.OrdinalIgnoreCase) && parts[^1].EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? parts[0] : null;
    }

    // ── Candidate → Test Case content ────────────────────────────────────────────────────────────────────────

    public static string AreaSuiteId(string? title) => "area:" + Slug(title ?? "other");

    /// <summary>The generated (read-only) content of a test case prepared from a candidate: steps and expected results only from the evidence.</summary>
    public static TestCaseContent Content(DiscoveredScenario c)
    {
        var group = c.Area ?? ScenarioCandidateTypes.Label(c.Type);
        var steps = ProcedureSteps(c);
        var interpreted = Interpret(c);
        var preconditions = c.Preconditions.Count > 0 ? string.Join("\n", c.Preconditions) : interpreted?.Preconditions;
        var testData = c.TestDataNeeds.Count > 0 ? string.Join("; ", c.TestDataNeeds) : interpreted?.TestDataRequirement;
        var questions = interpreted is null ? c.OpenQuestions
            : [.. c.OpenQuestions, .. interpreted.Unresolved, .. interpreted.Variants.Select(v => "Separate variant in the source (prepare as its own test case if needed): " + v)];
        return new TestCaseContent
        {
            Title = c.Title, Purpose = c.Purpose, Preconditions = preconditions,
            Steps = [.. steps.Select(s => s.Action)], StepExpected = [.. steps.Select(s => s.Expected)],
            ExpectedResult = c.ExpectedBehaviour, ExpectedBasis = Basis(c),
            RequiredTestData = testData,
            EnvironmentPrerequisites = c.Prerequisites.Count == 0 ? null : string.Join("\n", c.Prerequisites),
            Requirements = [.. c.RequirementIds.Select(id => new TestCaseRequirement(id, id, null))],
            NamedExistingTests = [.. c.ExistingTests.Select(t => t.Name)],
            StageId = AreaSuiteId(group), GroupTitle = group, CandidateType = c.Type, Confidence = c.Confidence,
            Template = [.. c.Template], Evidence = [.. c.Evidence], ExistingTests = [.. c.ExistingTests], OpenQuestions = [.. questions],
            TestDataNeed = testData is not null ? TestDataNeed.Required : TestDataNeed.Unknown, Capability = c.CapabilityId,
            Story = c.Story, NumberInStory = c.NumberInStory, Clarifications = [.. c.Clarifications],
            SourceIdentity = c.SourceIdentity.Length > 0 ? c.SourceIdentity : null, SnapshotKey = string.IsNullOrEmpty(c.SnapshotKey) ? null : c.SnapshotKey,
            Conflicts = c.Conflicts.Count > 0 ? [.. c.Conflicts] : null,
        };
    }

    /// <summary>
    /// The interpretation of a specification acceptance scenario (<see cref="AcceptanceScenarioInterpreter"/>), or null for other sources and for
    /// scenarios without a When. Built when a case is prepared (new drafts only), so the scenario's evidence fingerprint does not change.
    /// </summary>
    public static AcceptanceScenarioInterpreter.Interpretation? Interpret(DiscoveredScenario c)
    {
        if (c.CapabilityId != Specification || c.Steps.Count != 1) return null;
        var when = c.Template.FirstOrDefault(t => t.Key == "when")?.Value;
        if (string.IsNullOrWhiteSpace(when)) return null;
        var given = c.Template.FirstOrDefault(t => t.Key == "given")?.Value;
        var then = c.Template.FirstOrDefault(t => t.Key == "then")?.Value ?? c.ExpectedBehaviour;
        return AcceptanceScenarioInterpreter.Interpret(given, when, then, c.Story);
    }

    /// <summary>
    /// The manual procedure of a specification acceptance scenario (2026-11-24): the Given is the precondition and test-data requirement (not a
    /// generic "prepare the starting situation" step), the When is one concrete action with its pronoun resolved, and the Then is the list of
    /// verification points. How an arrival is sent, how processing is triggered and where a negative outcome can be checked are not in the
    /// specification, so they are listed as unresolved questions on the case — never invented. Other sources keep their own steps.
    /// </summary>
    public static List<DiscoveredStep> ProcedureSteps(DiscoveredScenario c)
    {
        if (Interpret(c) is not { } interpreted) return c.Steps;
        var expected = interpreted.Expected.Count > 0 ? string.Join("\n", interpreted.Expected) : c.Steps[0].Expected;
        return [new DiscoveredStep(interpreted.Action, expected)];
    }

    public static ExpectedResultBasis Basis(DiscoveredScenario c) => c.ExpectedBehaviour is null ? ExpectedResultBasis.Missing : c.CapabilityId switch
    {
        SystemGuide => c.Confidence == CandidateConfidence.ExplicitSpecification ? ExpectedResultBasis.ReviewedGuide : ExpectedResultBasis.UnreviewedGuide,
        ApiContract => ExpectedResultBasis.Contract,
        UiSource => ExpectedResultBasis.SourceDerived,
        _ => ExpectedResultBasis.Specification,
    };

    /// <summary>Suites for project cases: one per area / type group in first-seen order (real metadata, never a folder name).</summary>
    public static List<TestSuiteDraft> ProjectSuites(IEnumerable<TestCaseContent> cases) =>
        [.. cases.Select(c => (Id: "suite:" + (c.StageId ?? AreaSuiteId(null)), Name: c.GroupTitle ?? "Other")).DistinctBy(x => x.Id).Select(x => new TestSuiteDraft(x.Id, x.Name, "area", null))];

    private static string Slug(string value)
    {
        var slug = new string([.. value.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "other" : slug[..Math.Min(slug.Length, 60)];
    }
}
