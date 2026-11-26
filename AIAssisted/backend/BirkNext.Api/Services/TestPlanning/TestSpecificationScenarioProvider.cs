using BirkNext.TestPlanning;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Standalone documented tests (2026-11-26): the end-to-end tests written in a module's test specification document (e.g. «TEST-E-01» in
/// «HendelseService/docs/Hendelsestjenesten – Testspesifikasjon.md»), read once at Source Analysis into typed facts
/// (<see cref="IqrSourceSnapshot.DocumentedTests"/>). One candidate per test; the identity is the DOCUMENT plus the id (TEST-E-01 exists in several
/// modules). No user story is invented: these scenarios stand on their own. The level is what the document DECLARES (an E2E candidate, never a
/// verified boundary); the scenario type is classified per test like an acceptance scenario. Integration and unit tests of the documents are
/// developer tests and are not offered here.
/// </summary>
public sealed class TestSpecificationScenarioProvider : ITestScenarioCandidateProvider
{
    private const string Engine = "Test specification reader (documented tests)";
    public string CapabilityId => D.TestSpecification;

    /// <summary>The declared levels offered as manual scenarios.</summary>
    public static readonly IReadOnlySet<ScenarioTestLevel> Offered = new HashSet<ScenarioTestLevel> { ScenarioTestLevel.EndToEnd };

    public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        if (context.Snapshot is null) return Done(DiscoveryCapabilityState.NotApplicable, "No source snapshot is linked to this project.");
        if (context.Snapshot.DocumentedTests is not { } documented)
            return Done(DiscoveryCapabilityState.NoEvidence, "The source snapshot was analyzed before documented tests were read (re-import or re-analyze the source to read them).");
        var offered = documented.Tests.Where(t => Offered.Contains(t.DeclaredLevel)).ToList();
        var list = offered.Select(t => Candidate(context.ProjectKey, t)).ToList();
        var detail = documented.Documents.Count == 0 ? "No test specification document (… Testspesifikasjon / Test Specification …) in the source."
            : $"{documented.Tests.Count} documented tests in {documented.Documents.Count} test specification document(s); {list.Count} declared end-to-end test(s) offered.";
        return Task.FromResult(new CandidateProviderOutput(D.Capability(D.TestSpecification, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available,
            list.Count, detail, Engine,
            ["The test level is what the document declares; whether the test really crosses the end-to-end boundary is not verified.",
             "Documented tests are not linked to a user story unless the document says so; none is invented.", .. documented.Limitations]), list));
    }

    private static Task<CandidateProviderOutput> Done(DiscoveryCapabilityState state, string detail) =>
        Task.FromResult(new CandidateProviderOutput(D.Capability(D.TestSpecification, state, 0, detail, Engine), []));

    internal static DiscoveredScenario Candidate(string projectKey, DocumentedTest t)
    {
        var expected = t.Steps.Select(s => s.Expected).OfType<string>().ToList();
        var then = expected.Count == 0 ? null : string.Join("; ", expected);
        var when = t.Steps.Select(s => s.Action).FirstOrDefault(a => a.Length > 0);
        var given = t.Given.Count == 0 ? null : string.Join(" og ", t.Given);
        return D.FinishCandidate(new DiscoveredScenario
        {
            CapabilityId = D.TestSpecification, SourceCategory = "Test specification", Area = t.Module.Length > 0 ? t.Module : null,
            Title = $"{t.Id}: {t.Title}", Purpose = then is null ? $"Documented test {t.Id} of {t.File}." : $"Confirm that {then}",
            Type = t.DeclaredLevel == ScenarioTestLevel.EndToEnd ? ScenarioCandidateType.BusinessE2E : ScenarioCandidateType.Integration,
            Confidence = then is null ? CandidateConfidence.Insufficient : CandidateConfidence.ExplicitSpecification,
            ExpectedBehaviour = then, ExpectedBasis = then is null ? null : $"Documented test {t.Id} (Forventet)",
            Preconditions = [.. t.Given],
            Steps = [.. t.Steps.Where(s => s.Action.Length > 0 || s.Expected is not null).Select(s => new DiscoveredStep(s.Action, s.Expected))],
            Template = [new("given", "Starting situation (Given)", given), new("when", "Action (When)", when), new("then", "Expected outcome (Then)", then)],
            Evidence = [new("test-specification", $"{t.File}#{t.Id}", $"{t.Section} (line {t.Line})".Trim())],
            // Scoped by document: TEST-E-01 of HendelseService and of RevisjonService are two tests.
            CanonicalKey = $"testspec:{t.File}#{t.Id}", Engine = Engine,
        }, projectKey);
    }
}
