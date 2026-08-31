using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

[Trait("Category", "AuthenticatedReviewPhaseA5Focused")]
public sealed class AuthenticatedReviewPhaseA5OrchestrationTests
{
    [Fact]
    public async Task MissingAuthoritativeSnapshot_IsRejectedBeforeAnyEngineInvocation()
    {
        var fixture = new Fixture();

        var result = await fixture.Orchestrator.RunAsync(Fixture.Target, fixture.Context);

        fixture.Runtime.Calls.Should().Be(0);
        fixture.Accessibility.Calls.Should().Be(0);
        fixture.Lighthouse.Calls.Should().Be(0);
        fixture.PassiveSecurity.Calls.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.BrowserRuntime)
            .OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticationRequired);
    }

    [Fact]
    public async Task UnsupportedSelectedEngines_AreExplicitAndNeverInvoked()
    {
        var fixture = new Fixture();
        var result = await fixture.RunAsync();

        fixture.Lighthouse.Calls.Should().Be(0);
        fixture.PassiveSecurity.Calls.Should().Be(0);
        result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.Lighthouse)
            .OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported);
        result.QualityReport.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.PassiveSecurity)
            .OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported);
    }

    [Fact]
    public async Task RuntimeThenAccessibility_AssessesBothUsingSameCapturedReference()
    {
        var fixture = new Fixture();
        var result = await fixture.RunAsync();

        fixture.Runtime.Calls.Should().Be(1);
        fixture.Accessibility.Calls.Should().Be(1);
        fixture.Runtime.LastRequest!.AuthenticatedSessionId.Should().Be(Fixture.Reference.SessionId);
        fixture.Accessibility.LastRequest!.SessionId.Should().Be(Fixture.Reference.SessionId);
        result.QualityReport!.EngineOutcomes.Where(x => x.EngineId is FrontendQualityEngineId.BrowserRuntime or FrontendQualityEngineId.Accessibility)
            .Should().OnlyContain(x => x.ExecutionState == FrontendQualityEngineExecutionState.Assessed &&
                                      x.OutcomeReason == FrontendQualityEngineOutcomeReason.None);
    }

    [Theory]
    [InlineData(AuthenticatedBrowserSessionStatus.AuthenticationExpired, FrontendQualityEngineOutcomeReason.AuthenticationExpired)]
    [InlineData(AuthenticatedBrowserSessionStatus.UnexpectedOrigin, FrontendQualityEngineOutcomeReason.UnexpectedOrigin)]
    [InlineData(AuthenticatedBrowserSessionStatus.AuthenticationCancelled, FrontendQualityEngineOutcomeReason.AuthenticationCancelled)]
    [InlineData(AuthenticatedBrowserSessionStatus.Disposed, FrontendQualityEngineOutcomeReason.SessionUnavailable)]
    [InlineData(AuthenticatedBrowserSessionStatus.AuthenticationFailed, FrontendQualityEngineOutcomeReason.ResourceUnavailable)]
    public async Task InvalidBetweenEngines_ShortCircuitsAccessibilityAndRetainsRuntimeEvidence(
        AuthenticatedBrowserSessionStatus status,
        FrontendQualityEngineOutcomeReason expected)
    {
        var fixture = new Fixture { SessionStatus = status };

        var result = await fixture.RunAsync();

        fixture.Runtime.Calls.Should().Be(1);
        fixture.Accessibility.Calls.Should().Be(0);
        result.BrowserRuntimeReport!.Findings.Should().ContainSingle();
        result.QualityReport!.Findings.Should().ContainSingle(x => x.EngineId == FrontendQualityEngineId.BrowserRuntime);
        var accessibility = result.QualityReport.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.Accessibility);
        accessibility.OutcomeReason.Should().Be(expected);
        accessibility.ExecutionState.Should().NotBe(FrontendQualityEngineExecutionState.Assessed);
        result.AccessibilityReport!.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewCancelledBetweenEngines_ShortCircuitsAndRetainsRuntimeEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Orchestrator.AuthenticatedObserver = new CallbackObserver(cancellation.Cancel);

        var result = await fixture.RunAsync(cancellation.Token);

        fixture.Accessibility.Calls.Should().Be(0);
        result.BrowserRuntimeReport!.Findings.Should().ContainSingle();
        result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.Accessibility)
            .OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.Cancelled);
    }

    [Theory]
    [InlineData(BrowserRuntimeOutcomeReasonDto.None, FrontendQualityEngineOutcomeReason.None)]
    [InlineData(BrowserRuntimeOutcomeReasonDto.AuthenticationRequired, FrontendQualityEngineOutcomeReason.AuthenticationRequired)]
    [InlineData(BrowserRuntimeOutcomeReasonDto.AuthenticationExpired, FrontendQualityEngineOutcomeReason.AuthenticationExpired)]
    [InlineData(BrowserRuntimeOutcomeReasonDto.AuthenticationCancelled, FrontendQualityEngineOutcomeReason.AuthenticationCancelled)]
    [InlineData(BrowserRuntimeOutcomeReasonDto.UnexpectedOrigin, FrontendQualityEngineOutcomeReason.UnexpectedOrigin)]
    [InlineData(BrowserRuntimeOutcomeReasonDto.SessionUnavailable, FrontendQualityEngineOutcomeReason.SessionUnavailable)]
    public void BrowserRuntimeTypedReason_SurvivesNormalization(
        BrowserRuntimeOutcomeReasonDto source,
        FrontendQualityEngineOutcomeReason expected)
    {
        var status = source == BrowserRuntimeOutcomeReasonDto.None
            ? BrowserRuntimeEngineStatusDto.Assessed
            : BrowserRuntimeEngineStatusDto.Skipped;
        var outcome = FrontendQualityEngineOutcomeNormalizer.BrowserRuntime(
            Fixture.Target, true, Policy(), new(status, OutcomeReason: source), null, true);
        outcome.OutcomeReason.Should().Be(expected);
    }

    [Theory]
    [InlineData(AccessibilityOutcomeReasonDto.None, FrontendQualityEngineOutcomeReason.None)]
    [InlineData(AccessibilityOutcomeReasonDto.AuthenticationRequired, FrontendQualityEngineOutcomeReason.AuthenticationRequired)]
    [InlineData(AccessibilityOutcomeReasonDto.AuthenticationExpired, FrontendQualityEngineOutcomeReason.AuthenticationExpired)]
    [InlineData(AccessibilityOutcomeReasonDto.AuthenticationCancelled, FrontendQualityEngineOutcomeReason.AuthenticationCancelled)]
    [InlineData(AccessibilityOutcomeReasonDto.UnexpectedOrigin, FrontendQualityEngineOutcomeReason.UnexpectedOrigin)]
    public void AccessibilityTypedReason_SurvivesNormalization(
        AccessibilityOutcomeReasonDto source,
        FrontendQualityEngineOutcomeReason expected)
    {
        var status = source == AccessibilityOutcomeReasonDto.None
            ? AccessibilityExecutionStatusDto.Assessed
            : AccessibilityExecutionStatusDto.Skipped;
        var outcome = FrontendQualityEngineOutcomeNormalizer.Accessibility(
            Fixture.Target, true, Policy(), new(status, Findings: [], OutcomeReason: source), null, true);
        outcome.OutcomeReason.Should().Be(expected);
    }

    [Theory]
    [InlineData("layer1", FrontendQualityEngineOutcomeReason.BlockedByDeploymentPolicy)]
    [InlineData("layer2", FrontendQualityEngineOutcomeReason.DisabledInSystemSettings)]
    [InlineData("selected", FrontendQualityEngineOutcomeReason.NotSelected)]
    [InlineData("auth", FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported)]
    public async Task LayerSnapshotGate_DoesNotInvokeRuntimeAndPreservesReason(string layer, FrontendQualityEngineOutcomeReason expected)
    {
        var fixture = new Fixture();
        var snapshot = Fixture.Snapshot();
        if (layer == "layer1") snapshot.Layer1Allowed[FrontendQualityEngineIdDto.BrowserRuntime] = false;
        if (layer == "layer2") snapshot.Layer2Enabled[FrontendQualityEngineIdDto.BrowserRuntime] = false;
        if (layer == "selected") snapshot.SelectedEngines[FrontendQualityEngineIdDto.BrowserRuntime] = false;
        if (layer == "auth") snapshot.AuthModeSupported[FrontendQualityEngineIdDto.BrowserRuntime] = false;

        var result = await fixture.Orchestrator.RunAsync(Fixture.Target, fixture.Context, snapshot);

        fixture.Runtime.Calls.Should().Be(0);
        result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.BrowserRuntime)
            .OutcomeReason.Should().Be(expected);
    }

    [Fact]
    public async Task RuntimeNotReady_DoesNotInvokeAndIsNotAssessmentSuccess()
    {
        var fixture = new Fixture(runtimeReady: false);
        var result = await fixture.RunAsync();
        var runtime = result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == FrontendQualityEngineId.BrowserRuntime);
        fixture.Runtime.Calls.Should().Be(0);
        runtime.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.ReadinessUnavailable);
        runtime.ExecutionState.Should().NotBe(FrontendQualityEngineExecutionState.Assessed);
    }

    private static FrontendQualityEngineRequirementPolicy Policy() => new(
        Enum.GetValues<FrontendQualityEngineId>().ToDictionary(x => x, _ => FrontendQualityEngineRequirement.Optional));

    private sealed class Fixture
    {
        public const string Target = "https://example.test/app";
        public static readonly AuthenticatedBrowserExecutionReference Reference = new("session", "review", "profile", Target);
        public RuntimeSpy Runtime { get; } = new();
        public AccessibilitySpy Accessibility { get; } = new();
        public ExternalSpy Lighthouse { get; } = new();
        public ExternalSpy PassiveSecurity { get; } = new();
        public AuthenticatedBrowserSessionStatus SessionStatus { get => Sessions.Status; init => Sessions.Status = value; }
        public SessionSpy Sessions { get; } = new();
        public FrontendAnalysisContext Context { get; } = ContextForAuthentication();
        public FrontendQualityReviewOrchestrator Orchestrator { get; }

        public Fixture(bool runtimeReady = true)
        {
            var readiness = new Mock<IFrontendQualityEngineStatusApiService>();
            readiness.Setup(x => x.RevalidateEngineReadinessAsync(It.IsAny<FrontendQualityEngineIdDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FrontendQualityEngineIdDto engine, CancellationToken _) => new FrontendQualityEngineReadinessReportDto
                {
                    EngineId = engine,
                    IsAvailable = engine != FrontendQualityEngineIdDto.BrowserRuntime || runtimeReady,
                    CheckedAtUtc = DateTime.UtcNow
                });
            Orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
                runtime: Runtime,
                accessibility: Accessibility,
                lighthouse: Lighthouse,
                passiveSecurity: PassiveSecurity,
                authenticatedSessions: Sessions,
                readiness: readiness.Object);
        }

        public Task<FrontendQualityReviewOrchestrationResult> RunAsync(CancellationToken token = default) =>
            Orchestrator.RunAsync(Target, Context, Snapshot(), token);

        public static FrontendQualityEngineExecutionSnapshot Snapshot()
        {
            var snapshot = new FrontendQualityEngineExecutionSnapshot { AuthMode = ReviewAuthenticationModeDto.Authenticated };
            foreach (var engine in Enum.GetValues<FrontendQualityEngineIdDto>())
            {
                snapshot.Layer1Allowed[engine] = true;
                snapshot.Layer2Enabled[engine] = true;
                snapshot.SelectedEngines[engine] = true;
                snapshot.AuthModeSupported[engine] = engine is FrontendQualityEngineIdDto.BrowserRuntime or FrontendQualityEngineIdDto.Accessibility;
            }
            return snapshot;
        }

        private static FrontendAnalysisContext ContextForAuthentication() => new()
        {
            TargetUrl = Target,
            RequiresAuthentication = true,
            IsAuthenticatedSessionAvailable = true,
            ActiveProfile = new() { Id = "profile", TargetUrl = Target, Performance = new() },
            FeatureToggles = new()
            {
                EnableBrowserRuntimeEngine = true,
                EnableAccessibilityEngine = true,
                EnableLighthouseEngine = true,
                EnablePassiveSecurityEngine = true,
            },
            AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [],
            SecuritySettings = new(),
        };
    }

    private sealed class RuntimeSpy : IFrontendBrowserRuntimeReviewApiService
    {
        public int Calls { get; private set; }
        public BrowserRuntimeApiExecutionRequest? LastRequest { get; private set; }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int timeout = 30000, int shutdownTimeout = 5000, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Anonymous fallback forbidden");
        public Task<BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; LastRequest = request;
            return Task.FromResult(new BrowserRuntimeResultDto(
                BrowserRuntimeEngineStatusDto.Assessed,
                RequestedUrl: request.TargetUrl,
                Findings: [new("runtime-real", "Runtime finding", BrowserRuntimeFindingSeverityDto.Medium, "ConsoleError", "safe", "fix", ["safe evidence"])],
                ExecutionMode: BrowserRuntimeExecutionModeDto.AuthenticatedSessionPage));
        }
    }

    private sealed class AccessibilitySpy : IFrontendAccessibilityReviewApiService
    {
        public int Calls { get; private set; }
        public AccessibilityApiExecutionRequest? LastRequest { get; private set; }
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Anonymous fallback forbidden");
        public Task<AccessibilityResultDto> ReviewAsync(AccessibilityApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; LastRequest = request;
            return Task.FromResult(new AccessibilityResultDto(
                AccessibilityExecutionStatusDto.Assessed,
                RequestedUrl: request.TargetUrl,
                Findings: [new("button-name", AccessibilityFindingKindDto.Violation, FrontendQualitySeverity.Critical, "critical", "Button name", "safe", [], 1, [], [], [], null, "name it")],
                ExecutionMode: AccessibilityExecutionModeDto.AuthenticatedSessionPage));
        }
    }

    private sealed class ExternalSpy : IFrontendLighthouseReviewApiService, IFrontendPassiveSecurityApiService
    {
        public int Calls { get; private set; }
        public Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new LighthouseResultDto(LighthouseExecutionStatusDto.Assessed)); }
        public Task<PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PassiveSecurityResultDto(
                PassiveSecurityExecutionStatusDto.Assessed, "ZAP", "Passive", null, targetUrl, targetUrl,
                null, null, null, 0, 0, 0, 0, [], [], null, "Passive only", null));
        }
    }

    private sealed class SessionSpy : IAuthenticatedBrowserSessionService
    {
        public AuthenticatedBrowserSessionStatus Status { get; set; } = AuthenticatedBrowserSessionStatus.Authenticated;
        public Task<AuthenticatedBrowserExecutionReference?> GetExecutionReferenceAsync(FrontendAnalysisContext context) => Task.FromResult<AuthenticatedBrowserExecutionReference?>(Fixture.Reference);
        public Task<AuthenticatedBrowserSessionStatus> GetStatusAsync() => Task.FromResult(Status);
        public Task<AuthenticatedBrowserSessionStatus> GetStatusAsync(AuthenticatedBrowserExecutionReference reference, CancellationToken cancellationToken = default)
        { reference.Should().Be(Fixture.Reference); return Task.FromResult(Status); }
        public Task<AuthenticatedBrowserSession> GetOrCreateSessionAsync(FrontendAnalysisContext context) => throw new NotSupportedException();
        public Task<AuthenticatedBrowserSession?> GetCurrentSessionAsync() => throw new NotSupportedException();
        public Task<AuthenticatedBrowserSession> BeginAuthenticationAsync(FrontendAnalysisContext context) => throw new NotSupportedException();
        public Task ClearSessionAsync() => Task.CompletedTask;
    }

    private sealed class CallbackObserver(Action callback) : IAuthenticatedReviewOrchestrationObserver
    {
        public Task BetweenEnginesAsync(FrontendQualityEngineId completed, FrontendQualityEngineId next)
        { callback(); return Task.CompletedTask; }
    }
}
