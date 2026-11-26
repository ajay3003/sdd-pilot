using BirkNext.Api.Services.TestPlanning;
using BirkNext.Integrations;
using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Standalone documented tests (2026-11-26): TEST-E tests in a module's test specification document become scenarios of their own — identity
/// scoped by document, no invented user story, the DECLARED level kept apart from a verified boundary, and a scenario type of their own.
/// </summary>
public sealed class DocumentedTestScenarioTests
{
    private const string Hendelse = """
        Hendelsestjenesten – Testspesifikasjon
        ======================================

        3. Integrasjonstester
        ---------------------

        **TEST-I-20** — /helse/ready returnerer Frisk

            Gitt database oppe
            Når GET /helse/ready
            Forventet:
              - HTTP 200

        4. Ende-til-ende-tester
        -----------------------

        ### 4.1 Full innmatingsflyt med barnkobling

        **TEST-E-01** — Inngrep-hendelse fra adapter til GraphQL

            Gitt et barn registrert i Tjeneste med BirkTiltakPK = 12345 og BarnId = X

            Når PUT /innmating/inngrep/42 med BirkTiltakPK = 12345 og BarnId = X

            Og en bruker med alle fem Hendelse-operasjoner kaller hentHendelserForBarn(X)

            Forventet:
              - Hendelsen er synlig i tidslinjen
              - HendelsesRegistrert er publisert på hendelser.barn

        **TEST-E-02** — Inngrep-hendelse med forsinket barnkobling

            Gitt ingen registrert Tjeneste for BirkTiltakPK = 12345
            Når PUT /innmating/inngrep/42 med BirkTiltakPK = 12345 og BarnId = null
            Forventet: Hendelsen er IKKE synlig i GraphQL
            Når TjenesteOpprettet mottas med BirkTiltakPK = 12345 og BarnId = X
            Forventet:
              - Hendelsen er nå synlig i GraphQL for barnet med BarnId = X

        **TEST-E-03** — Leselogg-event publiseres ved visning

            Gitt en Hendelse synlig for bruker Y
            Når bruker Y kaller hentHendelserForBarn(barnId)
            Forventet:
              - Leselogg-event publisert på leselogg

        * * *
        """;

    private const string Revisjon = """
        5. End-to-end tests
        -------------------

        **TEST-E-01** — Revision of a report is stored

            Given a stored report
            When a correction arrives
            Then the corrected report replaces the previous one
        """;

    private static DocumentedTestSnapshot Read() => DocumentedTestReader.Read([
        ("HendelseService/docs/Hendelsestjenesten – Testspesifikasjon.md", Hendelse),
        ("RevisjonService/docs/M2LB.RevisjonService – Test-Specification.md", Revisjon),
        ("RevisjonService/specs/001-revisjonstjenesten-m01/plan.md", "Covers **TEST-E-01** — not defined here"),
    ]);

    [Fact]
    public void Tests_are_read_from_test_specification_documents_only_with_their_given_when_expected_and_declared_level()
    {
        var snap = Read();
        snap.Documents.Should().HaveCount(2, "plan.md mentions a TEST-E id but is not a test specification");
        snap.Tests.Select(t => (t.Module, t.Id, t.DeclaredLevel)).Should().Equal(
            ("HendelseService", "TEST-I-20", ScenarioTestLevel.Integration), ("HendelseService", "TEST-E-01", ScenarioTestLevel.EndToEnd),
            ("HendelseService", "TEST-E-02", ScenarioTestLevel.EndToEnd), ("HendelseService", "TEST-E-03", ScenarioTestLevel.EndToEnd),
            ("RevisjonService", "TEST-E-01", ScenarioTestLevel.EndToEnd));
        var e1 = snap.Tests.Single(t => t.Module == "HendelseService" && t.Id == "TEST-E-01");
        e1.Title.Should().Be("Inngrep-hendelse fra adapter til GraphQL");
        e1.Section.Should().Be("4.1 Full innmatingsflyt med barnkobling");
        e1.Given.Should().Equal("et barn registrert i Tjeneste med BirkTiltakPK = 12345 og BarnId = X");
        e1.Steps.Should().ContainSingle().Which.Should().Be(new DocumentedTestStep(
            "PUT /innmating/inngrep/42 med BirkTiltakPK = 12345 og BarnId = X og en bruker med alle fem Hendelse-operasjoner kaller hentHendelserForBarn(X)",
            "Hendelsen er synlig i tidslinjen; HendelsesRegistrert er publisert på hendelser.barn"));
        snap.Tests.Single(t => t.Id == "TEST-E-02").Steps.Select(s => s.Expected).Should().Equal("Hendelsen er IKKE synlig i GraphQL", "Hendelsen er nå synlig i GraphQL for barnet med BarnId = X");
        snap.Tests.Single(t => t.Module == "RevisjonService").Steps.Single().Expected.Should().Be("the corrected report replaces the previous one");
    }

    [Fact]
    public void Only_declared_E2E_tests_become_standalone_scenarios_scoped_by_document_with_no_story_and_their_own_type()
    {
        var snap = Read();
        var candidates = snap.Tests.Where(t => TestSpecificationScenarioProvider.Offered.Contains(t.DeclaredLevel))
            .Select(t => TestSpecificationScenarioProvider.Candidate("m2lb", t)).ToList();
        candidates.Should().HaveCount(4, "integration tests of the document are developer tests and not offered");
        candidates.Select(c => c.CandidateId).Should().OnlyHaveUniqueItems("TEST-E-01 of two modules is two scenarios");
        candidates.Select(c => c.SourceIdentity).Should().OnlyHaveUniqueItems();
        candidates.Should().OnlyContain(c => c.Story == null, "no user story is invented");
        candidates.Should().OnlyContain(c => c.Type == ScenarioCandidateType.BusinessE2E);
        TestLevelClassifier.Of(candidates[0].Type).Should().Match<TestLevelResult>(r => r.Level == ScenarioTestLevel.EndToEnd && r.Candidate,
            "the document DECLARES E2E; the boundary is not verified");
        candidates.Should().OnlyContain(c => ScenarioTypeClassifier.IsClassifiable(c), "each standalone test has its own scenario type");
        candidates.Select(c => ScenarioTypeClassifier.Classify(c, null, null).Type).Should().NotContainNulls();
        PlanStructure.FactsOf("x", candidates[0]).Module.Should().Be("HendelseService");
        PlanStructure.FactsOf("x", candidates[0]).Story.Should().BeNull();

        // Stable: the same document read again gives the same identities (re-import keeps plan membership).
        Read().Tests.Where(t => t.DeclaredLevel == ScenarioTestLevel.EndToEnd).Select(t => TestSpecificationScenarioProvider.Candidate("m2lb", t).CandidateId)
            .Should().Equal(candidates.Select(c => c.CandidateId));
    }

    [Fact]
    public async Task The_provider_says_honestly_when_the_snapshot_predates_documented_tests()
    {
        var provider = new TestSpecificationScenarioProvider();
        var none = await provider.DiscoverAsync(new ScenarioDiscoveryContext("m2lb", null, null, null, null), default);
        none.Capability.State.Should().Be(DiscoveryCapabilityState.NotApplicable);
        var old = await provider.DiscoverAsync(new ScenarioDiscoveryContext("m2lb", null, new IqrSourceSnapshot(), null, null), default);
        old.Capability.State.Should().Be(DiscoveryCapabilityState.NoEvidence);
        old.Candidates.Should().BeEmpty();
        var fresh = await provider.DiscoverAsync(new ScenarioDiscoveryContext("m2lb", null, new IqrSourceSnapshot { DocumentedTests = Read() }, null, null), default);
        fresh.Capability.State.Should().Be(DiscoveryCapabilityState.Available);
        fresh.Candidates.Should().HaveCount(4);
    }
}
