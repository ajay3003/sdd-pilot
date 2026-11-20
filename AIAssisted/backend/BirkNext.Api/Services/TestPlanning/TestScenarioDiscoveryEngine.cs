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
public sealed record CandidateProviderOutput(DiscoveryCapability Capability, IReadOnlyList<DiscoveredScenario> Candidates, IReadOnlyList<RequirementTestLink>? TestLinks = null);

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
    public const int MaxCandidates = 2000;

    public const string Specification = "specification";
    public const string SystemGuide = "system-guide";
    public const string UiSource = "ui-source";
    public const string ApiContract = "api-contract";
    public const string IntegrationJourneys = "integration-journeys";
    public const string ExistingTests = "existing-tests";

    /// <summary>"tsc-" + 16 hex of project | capability | canonical key. The snapshot is not part of it: the same scenario keeps its id across imports.</summary>
    public static string CandidateId(string projectKey, string capability, string canonicalKey) => "tsc-" + E.Sha($"{projectKey.Trim()}|{capability}|{canonicalKey}")[..16];

    private static string Clip(string? text, int max) => string.IsNullOrWhiteSpace(text) ? "" : text.Trim().Length <= max ? text.Trim() : text.Trim()[..(max - 1)] + "…";
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DiscoveredScenario Finish(DiscoveredScenario c, string projectKey) => c with
    {
        CandidateId = CandidateId(projectKey, c.CapabilityId, c.CanonicalKey), ProjectKey = projectKey, RuleVersion = RuleVersion,
        Title = Clip(c.Title, 300), Purpose = Clip(c.Purpose, 1000),
        Fingerprint = E.Sha(JsonSerializer.Serialize(new { c.Title, c.Purpose, c.Type, c.ExpectedBehaviour, c.Preconditions, c.Steps, c.Template, c.TestDataNeeds, c.RequirementIds }, E.Json)),
    };

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
        foreach (var s in workspace.AcceptanceScenarios.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
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
                Evidence = [new("specification", $"{file ?? "specification"}#{s.Id}", Clip(s.Title, 200)), .. s.RequirementIds.Select(r => new CandidateEvidenceRef("requirement", r))],
                CanonicalKey = $"scenario:{module}|{s.Id}", Engine = "Specification parser (acceptance scenarios)",
                OpenQuestions = [.. when is null ? new[] { "The scenario states no action (When): add the steps." } : [], .. then is null ? new[] { "The scenario states no outcome (Then): clarify the expected result." } : []],
            }, projectKey));
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
        }
        return new(Capability(Specification, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available, list.Count,
            list.Count == 0 ? "The selected specification has no acceptance scenarios or requirements the parser recognises."
                : $"{workspace.AcceptanceScenarios.Count} acceptance scenarios and {workspace.Requirements.Count} requirements{(module is null ? "" : $" ({module})")}.",
            "Specification parser"), list);
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

    public static List<DiscoveredScenario> FromGuide(string projectKey, SystemGuideRevision revision)
    {
        var guide = revision.Content;
        var reviewed = revision.State == GuideRevisionState.Published;
        var list = new List<DiscoveredScenario>();
        foreach (var s in guide.Scenarios)
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
                Evidence = [new("guide-scenario", $"{revision.GuideKey}/{s.Id}", $"Guide revision {revision.Revision}")],
                CanonicalKey = $"guide:{revision.GuideKey}/{s.Id}",
                ScopeKeys = ["Scenario:" + BirkNext.Interpretation.CanonicalKeys.EntityKey(BirkNext.Interpretation.CanonicalEntityType.Scenario,
                    BirkNext.Interpretation.CorrelationRules.GuideScenarioIdentity(revision.GuideKey, s.Id))],
                Engine = $"System Understanding guide r{revision.Revision}",
                OpenQuestions = [.. s.State == GuideScenarioState.NeedsClarification ? new[] { "The guide marks this scenario as needing clarification." } : []],
            }, projectKey));
        }
        return list;
    }

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
    public static List<DiscoveredScenario> FromPackJourneys(string projectKey, IIntegrationJourneyPack pack) => [.. pack.Journeys.SelectMany(j => j.Scenarios.Select(s =>
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
        .Select(r => new RequirementTestLink(r.Id, new CandidateTestAssociation(d.TestDefinitionId, d.DisplayName ?? d.MethodName, $"{d.FilePath}:{d.Line}",
            $"The test references {r.Id} ({r.Basis})", r.Confidence is TestEvidenceConfidence.Confirmed or TestEvidenceConfidence.StronglySupported))))];

    // ── Finalisation ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deduplicates on the canonical identity only (same canonical key → one candidate, evidence unioned), attaches existing tests by explicit
    /// requirement id, and notes "possible duplicate" between candidates of different sources that name the same requirement — never merged.
    /// Order: by source category, area, title.
    /// </summary>
    public static List<DiscoveredScenario> Finalize(string projectKey, string snapshotKey, IEnumerable<DiscoveredScenario> candidates, IEnumerable<RequirementTestLink> links)
    {
        var merged = candidates.Where(c => c.ProjectKey == projectKey).GroupBy(c => c.CandidateId, StringComparer.Ordinal).Select(g => g.Skip(1).Aggregate(g.First(), (a, b) => a with
        {
            Evidence = [.. a.Evidence.Concat(b.Evidence).DistinctBy(e => (e.Kind, e.Reference))],
            RequirementIds = [.. a.RequirementIds.Concat(b.RequirementIds).Distinct(StringComparer.OrdinalIgnoreCase)],
            ScopeKeys = [.. a.ScopeKeys.Concat(b.ScopeKeys).Distinct(StringComparer.Ordinal)],
        })).Take(MaxCandidates).Select(c => c with { SnapshotKey = snapshotKey }).ToList();
        var byRequirement = links.GroupBy(l => Norm(l.RequirementId)).ToDictionary(g => g.Key, g => g.Select(l => l.Test).ToList(), StringComparer.Ordinal);
        var sharing = merged.SelectMany(c => c.RequirementIds.Select(r => (Req: Norm(r), c.CandidateId, c.CapabilityId))).GroupBy(x => x.Req).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. merged.Select(c =>
        {
            var tests = c.RequirementIds.SelectMany(r => byRequirement.GetValueOrDefault(Norm(r)) ?? []).DistinctBy(t => t.TestId).Take(20).ToList();
            var duplicates = c.RequirementIds.SelectMany(r => sharing.GetValueOrDefault(Norm(r)) ?? []).Where(x => x.CandidateId != c.CandidateId && x.CapabilityId != c.CapabilityId)
                .Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Take(5).ToList();
            return c with { ExistingTests = tests, PossibleDuplicates = duplicates };
        }).OrderBy(c => c.SourceCategory, StringComparer.Ordinal).ThenBy(c => c.Area ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string Norm(string requirementId) => requirementId.Trim().ToUpperInvariant();

    // ── Candidate → Test Case content ────────────────────────────────────────────────────────────────────────

    public static string AreaSuiteId(string? title) => "area:" + Slug(title ?? "other");

    /// <summary>The generated (read-only) content of a test case prepared from a candidate: steps and expected results only from the evidence.</summary>
    public static TestCaseContent Content(DiscoveredScenario c)
    {
        var group = c.Area ?? ScenarioCandidateTypes.Label(c.Type);
        var steps = ProcedureSteps(c);
        return new TestCaseContent
        {
            Title = c.Title, Purpose = c.Purpose, Preconditions = c.Preconditions.Count == 0 ? null : string.Join("\n", c.Preconditions),
            Steps = [.. steps.Select(s => s.Action)], StepExpected = [.. steps.Select(s => s.Expected)],
            ExpectedResult = c.ExpectedBehaviour, ExpectedBasis = Basis(c),
            RequiredTestData = c.TestDataNeeds.Count == 0 ? null : string.Join("; ", c.TestDataNeeds),
            EnvironmentPrerequisites = c.Prerequisites.Count == 0 ? null : string.Join("\n", c.Prerequisites),
            Requirements = [.. c.RequirementIds.Select(id => new TestCaseRequirement(id, id, null))],
            NamedExistingTests = [.. c.ExistingTests.Select(t => t.Name)],
            StageId = AreaSuiteId(group), GroupTitle = group, CandidateType = c.Type, Confidence = c.Confidence,
            Template = [.. c.Template], Evidence = [.. c.Evidence], ExistingTests = [.. c.ExistingTests], OpenQuestions = [.. c.OpenQuestions],
            TestDataNeed = c.TestDataNeeds.Count > 0 ? TestDataNeed.Required : TestDataNeed.Unknown, Capability = c.CapabilityId,
        };
    }

    public const string PrepareStepPrefix = "Prepare the starting situation in the test environment: ";
    public const string PrepareStepExpected = "The starting situation is in place before the action (check the prepared data and state).";
    public const string ActionStepPrefix = "Carry out or trigger: ";

    /// <summary>
    /// The manual procedure of a specification acceptance scenario: first prepare its starting situation (Given), then carry out or trigger the
    /// action (When) with the stated outcome (Then) as its expected result. Nothing else is added: how the action is triggered in the environment
    /// and how the outcome is observed are not in the specification, so the case names them as needing clarification (never invented). Other
    /// sources keep their own steps. Built when a case is prepared, so the scenario's evidence fingerprint does not change.
    /// </summary>
    public static List<DiscoveredStep> ProcedureSteps(DiscoveredScenario c)
    {
        if (c.CapabilityId != Specification || c.Steps.Count != 1) return c.Steps;
        var given = c.Template.FirstOrDefault(t => t.Key == "given")?.Value;
        var when = c.Template.FirstOrDefault(t => t.Key == "when")?.Value;
        if (string.IsNullOrWhiteSpace(when)) return c.Steps;
        var action = new DiscoveredStep(ActionStepPrefix + when, c.Steps[0].Expected);
        return string.IsNullOrWhiteSpace(given) ? [action] : [new DiscoveredStep(PrepareStepPrefix + given, PrepareStepExpected), action];
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
