using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// Scenario ownership (2026-11-18). One candidate inventory (TestScenarioDiscoveryEngine) serves Manual Testing, Technical Testing and Test
// Management. This rule decides, from what each producer already records about a candidate (its capability and the candidate type that capability
// assigned), what the candidate is for and which workspace a tester would normally test it in. It never reads a title or a file name, never moves a
// saved case and never hides a candidate: every candidate stays in the complete inventory, and a technical candidate can still be prepared as a
// manual case on purpose.

/// <summary>What a candidate is for, in the terms a test lead uses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioPurpose
{
    BusinessFunctional, BusinessEndToEnd, UiFunctional, IntegrationFunctional, ApiContractTechnical, FrontendTechnical, PerformanceTechnical,
    SecurityTechnical, SupportingEvidence, Unclassified,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestingWorkspace { ManualTesting, TechnicalTesting }

/// <summary>The existing specialist module that owns technical work on a candidate (no new module is introduced).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecialistArea { None, ContractTesting, ApiQualityReview, IntegrationTesting, TestPackJourney, FrontendQualityReview, CriticalE2E, PerformanceTestReview }

/// <param name="Purpose">What the candidate is for.</param>
/// <param name="SourceKind">The producer's own kind of evidence in plain words ("Acceptance scenario", "API operation" …).</param>
/// <param name="Primary">The workspace a tester normally tests it in; null when the producer is not known (needs review).</param>
/// <param name="Secondary">The other workspace where it is also useful (explicit choice), or null.</param>
/// <param name="Specialist">The specialist module for technical verification, or <see cref="SpecialistArea.None"/>.</param>
/// <param name="Basis">Why: which recorded fact the classification rests on.</param>
public sealed record ScenarioOwnershipInfo(ScenarioPurpose Purpose, string SourceKind, TestingWorkspace? Primary, TestingWorkspace? Secondary, SpecialistArea Specialist, string Basis)
{
    public bool ManualRelevant => Primary == TestingWorkspace.ManualTesting;
    public bool Technical => Primary == TestingWorkspace.TechnicalTesting;
    public bool NeedsReview => Purpose == ScenarioPurpose.Unclassified;
}

public static class ScenarioOwnership
{
    // Capability ids of TestScenarioDiscoveryEngine (kept as literals here: the shared project does not reference the API).
    public const string Specification = "specification", SystemGuide = "system-guide", UiSource = "ui-source", ApiContract = "api-contract",
        IntegrationJourneys = "integration-journeys", ExistingTests = "existing-tests";

    public static ScenarioOwnershipInfo Of(DiscoveredScenario c)
    {
        const TestingWorkspace M = TestingWorkspace.ManualTesting, T = TestingWorkspace.TechnicalTesting;
        switch (c.CapabilityId)
        {
            case Specification:
                return c.CanonicalKey.StartsWith("requirement:", StringComparison.Ordinal)
                    ? new(ScenarioPurpose.BusinessFunctional, "Requirement without a scenario", M, null, SpecialistArea.None,
                        "A requirement of the specification that no acceptance scenario describes: business behaviour a person checks.")
                    : new(ScenarioPurpose.BusinessFunctional, "Acceptance scenario", M, null, SpecialistArea.None,
                        "An acceptance scenario (Given / When / Then) of the specification: business behaviour a person checks.");
            case SystemGuide:
                return c.Type == ScenarioCandidateType.BusinessE2E
                    ? new(ScenarioPurpose.BusinessEndToEnd, "Guide scenario", M, T, SpecialistArea.IntegrationTesting,
                        "A System Understanding guide scenario whose steps run through more than one system: a business end-to-end test, with the systems also verifiable in Integration Testing.")
                    : new(ScenarioPurpose.BusinessFunctional, "Guide scenario", M, null, SpecialistArea.None,
                        "A System Understanding guide scenario: business behaviour described for people.");
            case UiSource:
                return c.Type switch
                {
                    ScenarioCandidateType.UiSmoke => new(ScenarioPurpose.FrontendTechnical, "Page check", T, M, SpecialistArea.CriticalE2E,
                        "The UI analyzer found only that the page opens or navigates: a smoke check that belongs in automated Critical E2E regression."),
                    ScenarioCandidateType.BusinessE2E => new(ScenarioPurpose.BusinessEndToEnd, "UI journey", M, T, SpecialistArea.CriticalE2E,
                        "A user flow found in the UI source: human-observable, also suited to Critical E2E automation."),
                    _ => new(ScenarioPurpose.UiFunctional, "UI behaviour", M, T, SpecialistArea.FrontendQualityReview,
                        "A form, search, validation or action found in the UI source: human-observable behaviour; its technical quality is reviewed in Frontend Quality Review."),
                };
            case ApiContract:
                return c.Type == ScenarioCandidateType.SecurityAuthorization
                    ? new(ScenarioPurpose.SecurityTechnical, "Authorization check", T, M, SpecialistArea.ApiQualityReview,
                        "The contract declares security on this operation: an authorization check for API Quality Review.")
                    : new(ScenarioPurpose.ApiContractTechnical, "API operation", T, M, SpecialistArea.ContractTesting,
                        "An operation of an OpenAPI or GraphQL contract: verified against its contract in Contract Testing / API Quality Review.");
            case IntegrationJourneys:
                return new(ScenarioPurpose.IntegrationFunctional, "Integration journey", T, M,
                    IsPackJourney(c) ? SpecialistArea.TestPackJourney : SpecialistArea.IntegrationTesting,
                    "A journey through several systems: prepared and run in Integration Testing; a person can still prepare it as a manual end-to-end case.");
        }
        // A producer this rule does not know: classified by its own candidate type, or left for review (never guessed).
        return c.Type switch
        {
            ScenarioCandidateType.Performance => new(ScenarioPurpose.PerformanceTechnical, "Performance candidate", T, M, SpecialistArea.PerformanceTestReview, "Candidate type Performance."),
            ScenarioCandidateType.SecurityAuthorization => new(ScenarioPurpose.SecurityTechnical, "Security candidate", T, M, SpecialistArea.ApiQualityReview, "Candidate type Security / authorization."),
            _ => new(ScenarioPurpose.Unclassified, "Not classified", null, null, SpecialistArea.None,
                $"The producer \"{c.CapabilityId}\" is not known to the ownership rule: review where this candidate belongs."),
        };
    }

    public static string PurposeLabel(ScenarioPurpose purpose) => purpose switch
    {
        ScenarioPurpose.BusinessFunctional => "Business functional",
        ScenarioPurpose.BusinessEndToEnd => "Business end-to-end",
        ScenarioPurpose.UiFunctional => "UI functional",
        ScenarioPurpose.IntegrationFunctional => "Integration functional",
        ScenarioPurpose.ApiContractTechnical => "API / contract (technical)",
        ScenarioPurpose.FrontendTechnical => "Frontend (technical)",
        ScenarioPurpose.PerformanceTechnical => "Performance (technical)",
        ScenarioPurpose.SecurityTechnical => "Security / authorization (technical)",
        ScenarioPurpose.SupportingEvidence => "Supporting evidence",
        _ => "Unclassified — needs review",
    };

    public static string WorkspaceLabel(TestingWorkspace? workspace) => workspace switch
    {
        TestingWorkspace.ManualTesting => "Manual Testing",
        TestingWorkspace.TechnicalTesting => "Technical Testing",
        _ => "Not decided",
    };

    public static string SpecialistLabel(SpecialistArea area) => area switch
    {
        SpecialistArea.ContractTesting => "Contract Testing",
        SpecialistArea.ApiQualityReview => "API Quality Review",
        SpecialistArea.IntegrationTesting => "Integration Testing",
        SpecialistArea.TestPackJourney => "Integration journey (test pack)",
        SpecialistArea.FrontendQualityReview => "Frontend Quality Review",
        SpecialistArea.CriticalE2E => "Critical E2E Regression",
        SpecialistArea.PerformanceTestReview => "Performance Test Review",
        _ => "",
    };

    /// <summary>A test-pack journey scenario ("journey:{pack}:{journey}:{scenario}") rather than a journey a person declared for the project.</summary>
    public static bool IsPackJourney(DiscoveredScenario c) =>
        c.CapabilityId == IntegrationJourneys && JourneyParts(c) is { Scenario: not null } parts && !parts.Pack.StartsWith("project.", StringComparison.Ordinal);

    /// <summary>The pack, journey and (for pack scenarios) scenario of an integration-journey candidate, from its canonical key.</summary>
    public static (string Pack, string Journey, string? Scenario)? JourneyParts(DiscoveredScenario c)
    {
        if (!c.CanonicalKey.StartsWith("journey:", StringComparison.Ordinal)) return null;
        var id = c.CanonicalKey["journey:".Length..];
        // A declared journey's pack is "project.{projectKey}" and project keys may contain ':' ("import:import-…"); journey ids are slugs.
        if (id.StartsWith("project.", StringComparison.Ordinal))
        {
            var last = id.LastIndexOf(':');
            return last <= "project.".Length ? null : (id[..last], id[(last + 1)..], null);
        }
        var parts = id.Split(':');
        return parts.Length switch
        {
            >= 3 => (parts[0], parts[1], string.Join(':', parts[2..])),
            2 => (parts[0], parts[1], null),
            _ => null,
        };
    }

    /// <summary>
    /// The structural identity of a contract operation, for "the same operation declared in several contract files" (a GraphQL schema copied to a
    /// client, a contract-test snapshot, generated docs). GraphQL: operation + input + declared result. REST: the contract's own type, title and
    /// version (info.title / info.version, as the analyzer recorded them) + method and path — two services' /health/live are two endpoints, two
    /// copies of one OpenAPI document are one. (A path folder is not used: archives differ in their root folders.) Null for anything that is
    /// not an operation candidate. A shared identity is a relationship to show, never a merge.
    /// </summary>
    public static string? OperationIdentity(DiscoveredScenario c)
    {
        if (c.CapabilityId != ApiContract || c.Type != ScenarioCandidateType.ApiContract) return null;
        var operation = Field(c, "operation");
        if (operation is null) return null;
        var evidence = c.Evidence.FirstOrDefault(e => e.Kind == "contract-operation");
        if (evidence is null) return null;
        var graphQl = evidence.Detail?.StartsWith("GraphQl", StringComparison.Ordinal) == true;
        if (graphQl) return $"graphql|{operation}|{Field(c, "input")}|{Field(c, "expected-response")}";
        // Without a recorded title the document cannot be told apart from another service's: no shared identity (never guessed).
        if (string.IsNullOrWhiteSpace(evidence.Detail)) return $"rest|{evidence.Reference}";
        return $"rest|{evidence.Detail.Trim()}|{operation}";
    }

    /// <summary>The contract file of an operation candidate ("Svc/autodoc/openapi.yaml"), from its evidence reference "file:line#operation".</summary>
    public static string? ContractFile(DiscoveredScenario c)
    {
        var reference = c.Evidence.FirstOrDefault(e => e.Kind == "contract-operation")?.Reference;
        if (reference is null) return null;
        var hash = reference.IndexOf('#');
        var file = hash < 0 ? reference : reference[..hash];
        var colon = file.LastIndexOf(':');
        return colon > 1 && int.TryParse(file[(colon + 1)..], out _) ? file[..colon] : file;
    }

    private static string? Field(DiscoveredScenario c, string key) => c.Template.FirstOrDefault(t => t.Key == key)?.Value;
}
