using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Test work classification and the E2E rules (2026-11-28) ──────────────────────────────────────────────────────────────────────────────────
// Manual Testing and Technical Testing read ONE candidate inventory (TestScenarioDiscoveryEngine) and ONE plan/case store. This file decides, per
// candidate, which kind of test work it is and whether it is an end-to-end flow — from what the producers already recorded (capability, candidate
// type, evidence kind, contract type) and from a reviewer's explicit Manual/Technical decision. Never from a title, a file name or the source
// technology alone: a GraphQL call can be part of a manual end-to-end process, and a frontend check can be purely technical.
// Two separate, versioned rule sets: E2ERules (is it end-to-end, and how is it executed?) and TestWorkClassifier (which work category, which
// workspace shows it by default). Pure and offline; nothing is persisted, moved or hidden — the views filter, the records stay where they are.

/// <summary>The kind of test work, as the two workspaces sort it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestWorkCategory
{
    ManualBusiness, ManualE2E, TechnicalApi, TechnicalGraphQl, TechnicalIntegration, TechnicalE2E, Hybrid,
    /// <summary>Technical work outside API/GraphQL/integration/E2E (frontend quality, performance): Technical Testing, its specialist module.</summary>
    TechnicalOther,
    NeedsClassification,
}

/// <summary>Whether a candidate is an end-to-end flow, by the evidence behind it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum E2EStatus { Confirmed, Candidate, NotE2E, NeedsClarification }

/// <summary>How the work is executed: by a person, by a technical tester/automation, or both (one flow, two test variants).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionStyle { Manual, Technical, Hybrid }

/// <summary>The technical surface the evidence names (never inferred from a word in a title).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TechnicalSurface { None, Rest, GraphQl, Messaging, Ui }

/// <param name="Status">End-to-end status.</param>
/// <param name="Execution">How it is executed.</param>
/// <param name="Reasons">Why, in plain words: each recorded fact the result rests on (explainable, testable).</param>
public sealed record E2EAssessment(E2EStatus Status, TestExecutionStyle Execution, IReadOnlyList<string> Reasons, string RuleVersion)
{
    public bool IsE2E => Status is E2EStatus.Confirmed or E2EStatus.Candidate;
}

public sealed record TestWorkClassification(TestWorkCategory Category, E2EAssessment E2E, TechnicalSurface Surface, ScenarioOwnershipInfo Ownership, string RuleVersion)
{
    /// <summary>Shown by default in Manual Testing (manual business, manual E2E, hybrid).</summary>
    public bool ManualDefault => Category is TestWorkCategory.ManualBusiness or TestWorkCategory.ManualE2E or TestWorkCategory.Hybrid;
    /// <summary>Shown by default in Technical Testing (every technical category, hybrid, and what still needs classification).</summary>
    public bool TechnicalDefault => Category is not (TestWorkCategory.ManualBusiness or TestWorkCategory.ManualE2E);
}

/// <summary>
/// E2E rules, version <see cref="RuleVersion"/>. Status:
/// <list type="bullet">
/// <item><b>Confirmed</b>: the evidence itself is a chain through two or more systems — a declared integration journey (a person declared the component
/// chain) or a System Understanding guide scenario whose steps run through more than one system.</item>
/// <item><b>Candidate</b>: an end-to-end flow is documented or suggested but the boundary is not proven — a test specification's declared
/// end-to-end test (TEST-E-…), a user flow found in the UI source, an existing test or developer messaging test of type BusinessE2E/Integration.</item>
/// <item><b>NotE2E</b>: one operation, one page or one acceptance scenario that states no system boundary.</item>
/// <item><b>NeedsClarification</b>: a producer the rules do not know.</item>
/// </list>
/// Execution: a reviewer's explicit Manual/Technical/Both decision wins; otherwise the ownership rule's workspaces — a flow with a manual and a
/// technical workspace is Hybrid when it is end-to-end, else it is executed in its primary workspace.
/// </summary>
public static class E2ERules
{
    public const string RuleVersion = "e2e-rules/1";
    public const string TestSpecification = "test-specification";

    public static E2EAssessment Assess(DiscoveredScenario c, ScenarioTestKind? reviewed = null)
    {
        var reasons = new List<string>();
        var ownership = ScenarioOwnership.Of(c);
        E2EStatus status;
        switch (c.CapabilityId)
        {
            case ScenarioOwnership.IntegrationJourneys:
                status = E2EStatus.Confirmed;
                reasons.Add("Declared integration journey: a component chain through two or more systems.");
                break;
            case ScenarioOwnership.SystemGuide when c.Type == ScenarioCandidateType.BusinessE2E:
                status = E2EStatus.Confirmed;
                reasons.Add("Guide scenario whose steps run through more than one system.");
                break;
            case TestSpecification when c.Type == ScenarioCandidateType.BusinessE2E:
                status = E2EStatus.Candidate;
                reasons.Add("Declared end-to-end in a test specification document; the boundary is declared, not verified.");
                break;
            case ScenarioOwnership.UiSource when c.Type == ScenarioCandidateType.BusinessE2E:
                status = E2EStatus.Candidate;
                reasons.Add("User flow found in the UI source; the systems behind it are not shown by this evidence.");
                break;
            case ScenarioOwnership.DeveloperTests when c.Type == ScenarioCandidateType.Integration:
            case ScenarioOwnership.ExistingTests when c.Type is ScenarioCandidateType.BusinessE2E or ScenarioCandidateType.Integration:
                status = E2EStatus.Candidate;
                reasons.Add("An existing automated test through messaging or several components; an end-to-end boundary is suggested, not proven.");
                break;
            case ScenarioOwnership.Specification or ScenarioOwnership.SystemGuide or ScenarioOwnership.UiSource or ScenarioOwnership.ApiContract
                or ScenarioOwnership.DeveloperTests or ScenarioOwnership.ExistingTests or TestSpecification:
                status = E2EStatus.NotE2E;
                reasons.Add(c.CapabilityId switch
                {
                    ScenarioOwnership.ApiContract => "One operation of one service's contract.",
                    ScenarioOwnership.UiSource => "One page or function of one application.",
                    ScenarioOwnership.Specification => "An acceptance scenario that states no system boundary.",
                    TestSpecification => "A documented test that does not declare the end-to-end level.",
                    _ => "The evidence names one component.",
                });
                break;
            default:
                status = E2EStatus.NeedsClarification;
                reasons.Add($"The producer \"{c.CapabilityId}\" is not known to the E2E rules.");
                break;
        }

        TestExecutionStyle execution;
        if (reviewed is { } decision)
        {
            execution = decision switch { ScenarioTestKind.Manual => TestExecutionStyle.Manual, ScenarioTestKind.Technical => TestExecutionStyle.Technical, _ => TestExecutionStyle.Hybrid };
            reasons.Add($"Reviewed decision: {decision}.");
        }
        else if (c.CapabilityId == TestSpecification)
        {
            // A documented module test is written for technical execution (adapter → API → messages); a person can still run it as a manual E2E.
            execution = status == E2EStatus.NotE2E ? TestExecutionStyle.Technical : TestExecutionStyle.Hybrid;
            reasons.Add("A module's test specification: technical execution, a manual end-to-end variant is possible.");
        }
        else
        {
            var both = ownership.Primary is not null && ownership.Secondary is not null && ownership.Primary != ownership.Secondary;
            var endToEnd = status is E2EStatus.Confirmed or E2EStatus.Candidate;
            execution = both && endToEnd ? TestExecutionStyle.Hybrid
                : ownership.Primary == TestingWorkspace.TechnicalTesting ? TestExecutionStyle.Technical : TestExecutionStyle.Manual;
        }
        return new(status, execution, reasons, RuleVersion);
    }
}

/// <summary>Work classification, version <see cref="RuleVersion"/>, on top of <see cref="E2ERules"/> and <see cref="ScenarioOwnership"/>.</summary>
public static class TestWorkClassifier
{
    public const string RuleVersion = "test-work/1";

    /// <summary>The surface the evidence names: a contract operation's recorded contract type (OpenAPI → REST, GraphQL schema/operations →
    /// GraphQL), a messaging integration, or a UI source finding.</summary>
    public static TechnicalSurface SurfaceOf(DiscoveredScenario c)
    {
        if (c.CapabilityId == ScenarioOwnership.ApiContract)
            foreach (var e in c.Evidence.Where(e => e.Kind == "contract-operation"))
            {
                if (e.Detail?.StartsWith("GraphQl", StringComparison.Ordinal) == true) return TechnicalSurface.GraphQl;
                if (e.Detail?.StartsWith("OpenApi", StringComparison.Ordinal) == true) return TechnicalSurface.Rest;
            }
        if (c.CapabilityId == ScenarioOwnership.ApiContract) return TechnicalSurface.Rest; // a contract operation without a recorded type: REST is the analyzer's default
        if (c.Type == ScenarioCandidateType.Integration || c.CapabilityId == ScenarioOwnership.IntegrationJourneys) return TechnicalSurface.Messaging;
        if (c.CapabilityId == ScenarioOwnership.UiSource) return TechnicalSurface.Ui;
        return TechnicalSurface.None;
    }

    public static TestWorkClassification Classify(DiscoveredScenario c, ScenarioTestKind? reviewed = null)
    {
        var e2e = E2ERules.Assess(c, reviewed);
        var ownership = ScenarioOwnership.Of(c);
        var surface = SurfaceOf(c);
        var category = e2e.Status == E2EStatus.NeedsClarification && reviewed is null ? TestWorkCategory.NeedsClassification
            : e2e.IsE2E ? e2e.Execution switch
            {
                TestExecutionStyle.Hybrid => TestWorkCategory.Hybrid,
                TestExecutionStyle.Technical => TestWorkCategory.TechnicalE2E,
                _ => TestWorkCategory.ManualE2E,
            }
            : e2e.Execution == TestExecutionStyle.Hybrid ? TestWorkCategory.Hybrid
            : e2e.Execution == TestExecutionStyle.Manual ? TestWorkCategory.ManualBusiness
            : surface switch
            {
                TechnicalSurface.GraphQl => TestWorkCategory.TechnicalGraphQl,
                TechnicalSurface.Rest => TestWorkCategory.TechnicalApi,
                TechnicalSurface.Messaging => TestWorkCategory.TechnicalIntegration,
                _ => ownership.Purpose is ScenarioPurpose.IntegrationFunctional ? TestWorkCategory.TechnicalIntegration : TestWorkCategory.TechnicalOther,
            };
        return new(category, e2e, surface, ownership, RuleVersion);
    }

    public static string Label(TestWorkCategory category) => category switch
    {
        TestWorkCategory.ManualBusiness => "Manuell forretningstest",
        TestWorkCategory.ManualE2E => "Manuell E2E",
        TestWorkCategory.TechnicalApi => "REST/API",
        TestWorkCategory.TechnicalGraphQl => "GraphQL",
        TestWorkCategory.TechnicalIntegration => "Integrasjon",
        TestWorkCategory.TechnicalE2E => "Teknisk E2E",
        TestWorkCategory.Hybrid => "Manuell og teknisk",
        TestWorkCategory.TechnicalOther => "Annen teknisk",
        _ => "Må klassifiseres",
    };

    public static string Label(E2EStatus status) => status switch
    {
        E2EStatus.Confirmed => "Bekreftet E2E",
        E2EStatus.Candidate => "E2E-kandidat",
        E2EStatus.NotE2E => "Ikke E2E",
        _ => "Må avklares",
    };
}
