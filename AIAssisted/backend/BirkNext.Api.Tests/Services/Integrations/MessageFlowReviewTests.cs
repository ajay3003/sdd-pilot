using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.Integrations;

public sealed class MessageFlowReviewTests
{
    [Fact]
    public void DocumentedPilotKeepsEvidenceMaturityAndRepresentsTheIndirectPayloadAndHold()
    {
        var flow = MessageFlowReviewExamples.SkolenærværDocumented;
        flow.Nodes.Should().NotBeEmpty().And.OnlyContain(n => n.Evidence.State == FlowEvidenceState.Documented);
        flow.Hops.Should().OnlyContain(h => h.Evidence.State == FlowEvidenceState.Documented);
        flow.Hops.Should().OnlyContain(h => flow.Nodes.Any(n => n.Id == h.FromNodeId) && flow.Nodes.Any(n => n.Id == h.ToNodeId));
        flow.Nodes.Should().Contain(n => n.Name == "Skoletjenesten" && n.Role == FlowNodeRole.DomainService);
        flow.Nodes.Should().Contain(n => n.Name == "Person" && n.Role == FlowNodeRole.IdentityProvider);
        flow.ErrorPaths.Should().Contain(e => e.HoldPolicy != null && e.Owner == "Skoletjenesten (planned)");
        flow.ErrorPaths.Should().Contain(e => e.DeadLetterPolicy != null && e.Owner.Contains("broker", StringComparison.OrdinalIgnoreCase));
        flow.Hops.Should().Contain(h => h.PayloadMode == FlowPayloadMode.EncryptedReference);
        flow.Validation.Select(v => v.Layer).Should().Contain([FlowValidationLayer.Structural, FlowValidationLayer.DomainValidity, FlowValidationLayer.DataQuality]);
        flow.DataHandling!.Evidence.State.Should().Be(FlowEvidenceState.Documented);
        flow.Receipts.Should().Contain(r => r.ReturnPath.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
        flow.Deadlines.Should().Contain(d => d.TimeZone == "Europe/Oslo");
        flow.AuthorityNotes.Should().OnlyContain(n => n.State == FlowEvidenceState.Superseded && n.SupersededBy == "Skolenærvær — Spec-Kit Reference");
    }

    [Fact]
    public void IncompletePilotReadinessIsBlockedAndDoesNotReportFailedRuntime()
    {
        var readiness = IntegrationMessageFlowReadiness.Evaluate(MessageFlowReviewExamples.SkolenærværDocumented,
            MessageFlowReviewExamples.SkolenærværInitialConfiguration);

        readiness.CanRunActiveTest.Should().BeFalse();
        readiness.Blockers.Should().Contain(x => x.Contains("provider", StringComparison.OrdinalIgnoreCase));
        readiness.Items.Should().Contain(i => i.Key == "implementation" && !i.Satisfied);
        readiness.Items.Should().Contain(i => i.Key == "synthetic" && !i.Satisfied);
        MessageFlowReviewExamples.SkolenærværDocumented.Checkpoints.Should().OnlyContain(c => c.RuntimeStatus == FlowCheckpointStatus.NotAssessed);
    }

    [Fact]
    public async Task SafeConfigurationPersistsButCredentialLikeValuesAreRejected()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var store = new IntegrationMessageFlowStore(db);
        var flow = MessageFlowReviewExamples.GenericFixture with { EnvironmentId = "qa", TargetEnvironmentReference = "qa" };
        var safe = new IntegrationMessageFlowPackage(flow,
            new AltinnTestConfiguration { Environment = ExternalTestEnvironment.Test, ClientReference = "client-ref-qa", ProductionBlocked = false },
            new(false, 0, 0, [], []), [], []);

        var (saved, error) = await store.SaveAsync("qa", safe);
        error.Should().BeNull();
        saved!.AltinnConfiguration.ProductionBlocked.Should().BeTrue();
        saved.AltinnConfiguration.ConfigurationState.Should().Be(AltinnConfigurationState.Configured);
        saved.AltinnConfiguration.TargetEnvironmentId.Should().Be("qa");
        (await store.GetAsync("qa")).Flow.Hops.Should().HaveCount(flow.Hops.Count);

        var unsafePackage = safe with { AltinnConfiguration = safe.AltinnConfiguration with { ReporterReference = "client_secret=do-not-store" } };
        var (rejected, reason) = await store.SaveAsync("qa", unsafePackage);
        rejected.Should().BeNull();
        reason.Should().Contain("Credential-like");
        var rawIdentity = safe with { AltinnConfiguration = safe.AltinnConfiguration with { SyntheticIdentitySetReference = "12345678901" } };
        (await store.SaveAsync("qa", rawIdentity)).Package.Should().BeNull("raw personal identifiers must never be stored as test-data references");

        var spoofed = safe with { Flow = flow with
        {
            Nodes = flow.Nodes.Select(n => n with { Evidence = n.Evidence with { State = FlowEvidenceState.RuntimeObserved } }).ToList(),
            Checkpoints = flow.Checkpoints.Select(c => c with { RuntimeStatus = FlowCheckpointStatus.Observed }).ToList(),
        }, AltinnConfiguration = safe.AltinnConfiguration with { VerifiedScopes = ["spoofed"], VerificationState = FlowEvidenceState.AssertionPassed } };
        var (unverified, _) = await store.SaveAsync("qa", spoofed);
        unverified!.Flow.Nodes.Should().OnlyContain(n => n.Evidence.State == FlowEvidenceState.NotAssessed);
        unverified.Flow.Checkpoints.Should().OnlyContain(c => c.RuntimeStatus == FlowCheckpointStatus.NotAssessed);
        unverified.AltinnConfiguration.VerifiedScopes.Should().BeEmpty();
        unverified.AltinnConfiguration.VerificationState.Should().Be(FlowEvidenceState.NotAssessed);
    }

    [Fact]
    public void ManualPlanPreservesUnassessedCheckpointsAndSeparatesTransportFromBusinessCompletion()
    {
        var lines = IntegrationMessageFlowReadiness.ManualPlan(MessageFlowReviewExamples.SkolenærværDocumented,
            MessageFlowReviewExamples.SkolenærværInitialConfiguration);
        lines.Should().Contain(x => x.Contains("NotConfigured") && x.Contains("NotAssessed"));
        lines.Should().Contain(x => x.Contains("does not establish business validity", StringComparison.OrdinalIgnoreCase));
        lines.Should().Contain(x => x.Contains("Needs clarification"));
    }

    [Fact]
    public void GenericFixtureGeneratesCasesFromFlowValidationErrorsAndDataBoundaries()
    {
        var cases = MessageFlowTestCaseDesigner.Generate(MessageFlowReviewExamples.GenericFixture);
        cases.Should().Contain(c => c.Name == "Documented happy path");
        cases.Should().Contain(c => c.Name.Contains("Transient dependency unavailable"));
        cases.Should().Contain(c => c.Name.Contains("Unknown identity"));
        cases.Should().OnlyContain(c => c.EvidenceBasis.Length > 0 && c.Limitations.Length > 0);
    }
}
