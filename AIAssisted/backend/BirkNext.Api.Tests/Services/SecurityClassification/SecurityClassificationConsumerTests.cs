using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.TestData;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.SecurityClassification;

/// <summary>
/// Security Classification as a shared capability: one stored review projected onto shared requirements that M01 and Skolenærvær both
/// consume (assessments may differ, evidence is the same), source ≠ runtime, a runtime result proves its surfaces only, source/runtime
/// disagreement is Review required, results recorded under another test context are not current, a missing context or identity reference is a
/// prerequisite (owned by the review / Test Data), and domain rules stay with their consumer.
/// </summary>
public sealed class SecurityClassificationConsumerTests
{
    private static readonly ClassificationContextSummary Context = new()
    {
        Environment = "DEV", Approved = true, EndpointConfigured = true, ConfiguredLevels = [2, 3], UnauthorizedIdentityConfigured = true, AuthorizedIdentityConfigured = true,
    };

    private static ClassificationReviewResult Run(ClassificationState authorization, params ClassificationObservation[] observations) => new()
    {
        RunId = Guid.Parse("5ec00000-0000-0000-0000-000000000020"), EnvironmentId = "dev", CompletedAt = DateTimeOffset.Parse("2026-10-22T08:00:00Z"),
        Levels = [new() { Nivaa = 2 }, new() { Nivaa = 3 }],
        Summary =
        [
            new(ClassificationArea.Model, "Model", ClassificationState.SourceVerified, "source"),
            new(ClassificationArea.Pipeline, "Pipeline", ClassificationState.SourceVerified, "source"),
            new(ClassificationArea.DirectAccess, "Direct access", authorization, "source"),
        ],
        Live = new ClassificationLiveEvidence { Observations = [.. observations] },
        ContextSummary = Context,
    };

    private static ClassificationObservation Obs(ClassificationIdentity identity, ClassificationState state) =>
        new() { Nivaa = 3, Identity = identity, Surface = ClassificationSurface.DirectProfile, State = state, Expected = "denied", Observed = "denied" };

    private static SecurityRequirementAssessment Shared(SecurityRequirementKind kind, ClassificationReviewResult? run, ClassificationContextSummary? context = null, int? identities = 1) =>
        SecurityClassificationAssessments.Shared(kind, run, context ?? Context, identities);

    [Fact]
    public void Source_evidence_is_not_runtime_verification_and_a_runtime_result_proves_its_surfaces_only()
    {
        var run = Run(ClassificationState.SourceVerified, Obs(ClassificationIdentity.Unauthorized, ClassificationState.Pass));
        var propagation = Shared(SecurityRequirementKind.ClassificationPropagation, run);
        propagation.State.Should().Be(SecurityRequirementState.SourceEvidence);
        propagation.RuntimeTest.Should().Contain("Not verified at runtime");

        var denial = Shared(SecurityRequirementKind.UnauthorizedRuntimeDenial, run);
        denial.State.Should().Be(SecurityRequirementState.Verified);
        denial.RuntimeTest.Should().Contain("proves these surfaces only, never all authorization");
        Shared(SecurityRequirementKind.AuthorizedRuntimeAccess, run).State.Should().Be(SecurityRequirementState.NotTested, "no authorized observation was made");
    }

    [Fact]
    public void Source_and_runtime_disagreeing_is_review_required_and_a_runtime_leak_is_an_issue()
    {
        Shared(SecurityRequirementKind.UnauthorizedRuntimeDenial, Run(ClassificationState.Fail, Obs(ClassificationIdentity.Unauthorized, ClassificationState.Pass)))
            .State.Should().Be(SecurityRequirementState.ReviewRequired);
        Shared(SecurityRequirementKind.UnauthorizedRuntimeDenial, Run(ClassificationState.SourceVerified, Obs(ClassificationIdentity.Unauthorized, ClassificationState.Fail)))
            .State.Should().Be(SecurityRequirementState.IssueDetected);
        Shared(SecurityRequirementKind.AuthorizationSource, Run(ClassificationState.Fail)).State.Should().Be(SecurityRequirementState.IssueDetected);
    }

    [Fact]
    public void Runtime_results_recorded_under_another_test_context_are_not_current()
    {
        var run = Run(ClassificationState.SourceVerified, Obs(ClassificationIdentity.Authorized, ClassificationState.Pass));
        var changed = Context with { Environment = "QA" };
        var access = Shared(SecurityRequirementKind.AuthorizedRuntimeAccess, run, changed);
        access.State.Should().Be(SecurityRequirementState.NotCurrent);
        access.RuntimeTest.Should().Contain("not current");
        SecurityClassificationAssessments.RuntimeCurrent(run, null).Should().BeFalse("without a current context nothing runtime is current");
    }

    [Fact]
    public void A_missing_test_context_or_identity_reference_is_a_prerequisite_with_its_owner()
    {
        var context = Shared(SecurityRequirementKind.TestContext, null, Context with { Approved = false });
        context.State.Should().Be(SecurityRequirementState.Missing);
        context.SharedEvidence.Should().Contain("a prerequisite, not a security defect");
        var identities = Shared(SecurityRequirementKind.IdentityReferences, null, identities: 0);
        identities.State.Should().Be(SecurityRequirementState.Missing);
        identities.Owner.Should().Be(SecurityClassificationAssessments.TestDataOwner, "controlled identities are owned by Test Data");
        Shared(SecurityRequirementKind.ClassificationModel, null).State.Should().Be(SecurityRequirementState.NotTested);
    }

    [Fact]
    public void M01_and_Skolenaervaer_consume_the_same_run_with_different_assessments_and_domain_rules_stay_with_the_consumer()
    {
        var run = Run(ClassificationState.SourceVerified, Obs(ClassificationIdentity.Unauthorized, ClassificationState.Pass));
        var m01 = SecurityClassificationAssessments.For(new M01SecurityClassificationConsumer().Definition, run, Context, 1);
        var skole = SecurityClassificationAssessments.For(new SkoleNaervaerSecurityConsumer().Definition, run, Context, 1);

        (m01.RunId, skole.RunId).Should().Be((run.RunId, run.RunId), "both reference the same shared evidence");
        m01.Requirements.Should().NotContain(r => r.DomainSpecific);
        skole.Requirements.Where(r => r.DomainSpecific).Select(r => r.Key).Should().Equal("protected-downstream-handling", "bufdata-exclusion");
        skole.Requirements.Where(r => r.DomainSpecific).Should().OnlyContain(r => r.State == SecurityRequirementState.NotTested, "a shared review never verifies a domain rule");
        skole.Requirements.Select(r => r.Key).Should().NotContain("classification-model", "Skolenærvær consumes only what its journeys depend on");
        m01.Summary.Should().NotBe(skole.Summary);
        var shared = m01.Requirements.Single(r => r.Key == "unauthorized-access");
        skole.Requirements.Single(r => r.Key == "unauthorized-access").Should().Be(shared, "the shared requirement is identical for every consumer");
    }

    [Fact]
    public async Task The_service_reads_the_latest_review_the_canonical_context_and_Test_Data_identity_references()
    {
        var reviews = new Mock<IClassificationReviewService>();
        reviews.Setup(r => r.OverviewAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new ClassificationOverview
        {
            Latest = Run(ClassificationState.SourceVerified),
            Context = new ClassificationTestContext { Environment = "DEV", ApprovedByTestLead = true, GraphQlEndpoint = "https://dev.example.invalid/graphql", UnauthorizedIdentityLabel = "reader" },
        });
        var catalog = new TestDataCatalog([new Provider(new TestDataAsset { Id = "k6", Alias = "kode6-child", Kind = TestDataKind.EntityReference, Approval = TestDataApproval.Approved,
            Capabilities = [SecurityClassificationConsumerService.ProtectedIdentityCapability], Environment = "DEV" })]);
        var service = new SecurityClassificationConsumerService(reviews.Object, [new M01SecurityClassificationConsumer(), new SkoleNaervaerSecurityConsumer()], catalog);

        var all = await service.AssessAsync("dev", default);

        all.Select(a => a.ConsumerId).Should().Equal("m01", "skolenaervaer");
        all.Should().OnlyContain(a => a.Requirements.Single(r => r.Key == "identity-references").State == SecurityRequirementState.SourceEvidence);
        all.Should().OnlyContain(a => a.Requirements.Single(r => r.Key == "test-context").State == SecurityRequirementState.SourceEvidence);
        reviews.Verify(r => r.RunAsync(It.IsAny<string>(), It.IsAny<ClassificationRunRequest>(), It.IsAny<CancellationToken>()), Times.Never, "assessing never runs a review");
    }

    private sealed class Provider(TestDataAsset asset) : ITestDataProvider
    {
        public string Id => "fixture";
        public string Label => "Fixture";
        public string Description => "Test fixture";
        public IReadOnlyList<TestDataAsset> Assets() => [asset];
    }
}
