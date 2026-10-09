using System.Text.RegularExpressions;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ActiveEventTesting.Observation;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using FluentAssertions;
using H = BirkNext.Api.Tests.Services.ActiveEventTesting.ActiveEventTestHarness;

namespace BirkNext.Api.Tests.Services.ActiveEventTesting;

/// <summary>Continuity, consumer activity, the downstream verifier base over a FAKE gateway, final status rules and the core's domain neutrality.</summary>
public sealed class ActiveEventObservationTests
{
    private static ActiveEventObservationContext Context(H h, string? consumer = "$Default", string? role = "ca-person-adapter") => new(
        new ActiveEventTrustedTarget { TransportType = "EventHub", Resource = H.Hub, Consumer = consumer, ConsumerRole = role, EnvironmentType = "Development" },
        h.Catalog.Integration, h.Catalog.Platform);

    [Fact]
    public async Task Continuity_ComparesBaselineWithPostSendCheckpoints_AndNeverClaimsDownstreamSuccess()
    {
        await using var h = new H();
        var provider = new EventHubCheckpointContinuityProvider(h.Metadata, h.Checkpoints, h.Policy(), TimeProvider.System);
        var context = Context(h);
        provider.CanObserve(context, out _).Should().BeTrue();
        var baseline = await provider.CaptureBaselineAsync(context, default);
        baseline.Available.Should().BeTrue();
        baseline.Detail.Should().Contain("0@10");

        var evidence = await provider.ObserveAfterSendAsync(context, baseline, [new ActiveEventCorrelation { EventId = "e" }], TimeSpan.Zero, default);
        evidence.Stage.Should().Be(ActiveEventEvidenceStage.ConsumerContinuityObserved);
        evidence.Status.Should().Be(ActiveEventEvidenceStatus.Observed);
        evidence.Detail.Should().Contain("partition 0 (≥ 11)").And.Contain("does not show that any event was handled successfully or stored");
        evidence.CorrelationQuality.Should().Be(ActiveEventCorrelationQuality.Moderate);
        evidence.Downstream.Should().BeNull();

        var outcome = ActiveEventRunOutcome.Status(new ActiveEventScenarioDescriptor { RequiresDownstreamVerification = true, RequiresContinuity = true }, 1,
            [new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Observed }, evidence,
             new() { Stage = ActiveEventEvidenceStage.DownstreamVerified, Status = ActiveEventEvidenceStatus.NotVerified }]);
        outcome.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence, "checkpoint progression never sets DownstreamVerified");
    }

    [Fact]
    public async Task Continuity_StalledOrUnobservable_IsNotObservedOrUnavailable_NeverAFailure()
    {
        await using var h = new H();
        h.Checkpoints.After = h.Checkpoints.Before;
        var provider = new EventHubCheckpointContinuityProvider(h.Metadata, h.Checkpoints, h.Policy(), TimeProvider.System);
        var context = Context(h);
        var stalled = await provider.ObserveAfterSendAsync(context, await provider.CaptureBaselineAsync(context, default), [new()], TimeSpan.Zero, default);
        stalled.Status.Should().Be(ActiveEventEvidenceStatus.NotObserved);
        stalled.Detail.Should().Contain("not a failure");
        provider.CanObserve(Context(h, consumer: null), out var reason).Should().BeFalse();
        reason.Should().Contain("No consumer group");
        h.Metadata.State = IntegrationEvidenceState.NotConfigured;
        provider.CanObserve(context, out reason).Should().BeFalse();
        reason.Should().StartWith("Partition positions:");
    }

    [Fact]
    public async Task ConsumerActivity_IsAggregateOnly_AndSaysSo()
    {
        await using var h = new H();
        var provider = new TelemetryConsumerActivityProvider(h.Telemetry, TimeProvider.System);
        provider.CanObserve(Context(h), out var reason).Should().BeFalse();
        reason.Should().Contain("Consumer telemetry");
        h.Telemetry.State = IntegrationEvidenceState.Available;
        provider.CanObserve(Context(h, role: null), out reason).Should().BeFalse();
        reason.Should().Contain("consumer role");

        var sentFrom = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.Telemetry.LastActivity = DateTimeOffset.UtcNow;
        var observed = await provider.ObserveAsync(Context(h), sentFrom, TimeSpan.Zero, default);
        observed.Status.Should().Be(ActiveEventEvidenceStatus.Observed);
        observed.CorrelationQuality.Should().Be(ActiveEventCorrelationQuality.AggregateOnly);
        observed.Detail.Should().Contain("not evidence that a specific generated event was processed");
        h.Telemetry.LastActivity = sentFrom.AddHours(-1);
        (await provider.ObserveAsync(Context(h), sentFrom, TimeSpan.Zero, default)).Status.Should().Be(ActiveEventEvidenceStatus.NotObserved);
    }

    [Theory]
    [InlineData(200, ActiveEventDownstreamOutcome.Verified)]
    [InlineData(404, ActiveEventDownstreamOutcome.NotVerified)]
    [InlineData(401, ActiveEventDownstreamOutcome.Unavailable)]
    [InlineData(403, ActiveEventDownstreamOutcome.Unavailable)]
    [InlineData(300, ActiveEventDownstreamOutcome.Ambiguous)]
    [InlineData(409, ActiveEventDownstreamOutcome.Ambiguous)]
    [InlineData(500, ActiveEventDownstreamOutcome.UnexpectedResult)]
    public async Task DownstreamVerifier_MapsReadOnlyGatewayOutcomes(int status, ActiveEventDownstreamOutcome expected)
    {
        var gateway = new FakeGateway { Status = status };
        var verifier = new ExampleVerifier(gateway, new FixedIdentity());
        var result = await verifier.VerifyAsync(new ActiveEventTrustedTarget(), new ActiveEventScenarioDescriptor { ExtensionId = "example" },
            new ActiveEventCorrelation { ExpectedResourceIdentity = "rec-1" }, TimeSpan.Zero, default);
        result.Outcome.Should().Be(expected);
        result.ExpectedIdentity.Should().Be("rec-1");
        gateway.Calls.Should().OnlyContain(call => call.Method == "GET" && call.Url == "https://utdanning-dev.example.test/records/rec-1");
    }

    [Fact]
    public async Task DownstreamVerifier_WithoutAnAuthenticatedContextOrIdentity_IsUnavailable()
    {
        var unavailable = new ExampleVerifier(new FakeGateway { Executed = false }, new FixedIdentity());
        (await unavailable.VerifyAsync(new(), new() { ExtensionId = "example" }, new() { ExpectedResourceIdentity = "r" }, TimeSpan.Zero, default)).Outcome
            .Should().Be(ActiveEventDownstreamOutcome.Unavailable);
        var noIdentity = new ExampleVerifier(new FakeGateway(), new FixedIdentity(available: false));
        noIdentity.CanVerify(new(), new() { ExtensionId = "example" }, out var reason).Should().BeFalse();
        reason.Should().Contain("machine identity");
    }

    [Fact]
    public void Outcome_TransportOnlyIsNeverCompleted_AndUnexpectedDownstreamFails()
    {
        var scenario = new ActiveEventScenarioDescriptor { RequiresDownstreamVerification = true };
        ActiveEventStageEvidence Stage(ActiveEventEvidenceStage stage, ActiveEventEvidenceStatus status) => new() { Stage = stage, Status = status };
        var accepted = Stage(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Observed);
        ActiveEventRunOutcome.Status(scenario, 1, [accepted]).Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence);
        ActiveEventRunOutcome.Status(scenario, 1, [accepted, Stage(ActiveEventEvidenceStage.DownstreamVerified, ActiveEventEvidenceStatus.Observed)]).Should().Be(ActiveEventRunStatus.Completed);
        ActiveEventRunOutcome.Status(scenario, 1, [accepted, Stage(ActiveEventEvidenceStage.DownstreamVerified, ActiveEventEvidenceStatus.UnexpectedResult)]).Should().Be(ActiveEventRunStatus.Failed);
        ActiveEventRunOutcome.Status(scenario, 1, [accepted, Stage(ActiveEventEvidenceStage.DownstreamVerified, ActiveEventEvidenceStatus.Ambiguous)]).Should().Be(ActiveEventRunStatus.Inconclusive);
        ActiveEventRunOutcome.Status(scenario, 2, [accepted, Stage(ActiveEventEvidenceStage.DownstreamVerified, ActiveEventEvidenceStatus.Observed)]).Should().Be(ActiveEventRunStatus.Inconclusive, "one of two events was not confirmed");
        ActiveEventRunOutcome.Status(scenario, 1, [Stage(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Ambiguous)]).Should().Be(ActiveEventRunStatus.Inconclusive);
    }

    [Fact]
    public void Architecture_OneSendPath_AndTheGenericCoreIsDomainNeutral()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var api = Path.Combine(root, "BirkNext.Api");
        string Code(string file) => string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));
        var sources = Directory.GetFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        sources.Where(f => Regex.IsMatch(Code(f), @"\bEventHubProducerClient\b|\bEventHubBufferedProducerClient\b")).Select(Path.GetFileName).Should().Equal(["EventHubTestSender.cs"], "one producer in the whole API");
        sources.Where(f => Regex.IsMatch(Code(f), @"new\s+ApprovedEventHubDestination\(")).Select(Path.GetFileName).Should().Equal(["ActiveEventPolicy.cs"], "only the policy approves a destination");
        sources.Where(f => Regex.IsMatch(Code(f), @"class\s+\w*Runner\b") && f.Contains("ActiveEvent")).Select(Path.GetFileName).Should().Equal(["ActiveEventExecutionRunner.cs"], "one runner");
        sources.Where(f => Regex.IsMatch(Code(f), @"\bnew\s+HttpClient\(") && f.Contains($"{Path.DirectorySeparatorChar}ActiveEventTesting{Path.DirectorySeparatorChar}")).Should().BeEmpty("no bespoke downstream HTTP client");

        var core = Directory.GetFiles(Path.Combine(api, "Services", "ActiveEventTesting"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Providers{Path.DirectorySeparatorChar}"))
            .Append(Path.Combine(api, "Controllers", "ActiveEventsController.cs")).ToList();
        var domain = new Regex(@"PersonPK|PersonPersisted|Fornavn|Foedselsnummer|Fødselsnummer|Skole|Utdanning|ManglendeSkoletilbud|M2LB|m2lb");
        foreach (var file in core) domain.Matches(Code(file)).Select(m => m.Value).Should().BeEmpty($"{Path.GetFileName(file)} is generic core");
        var forbidden = new Regex(@"\b(ServiceBusSender|ServiceBusReceiver|EventProcessorClient|ReadEventsAsync|UpdateCheckpointAsync|BlobLeaseClient|UploadAsync|ConnectionString|SharedAccessKey|AzureNamedKeyCredential|AzureSasCredential|SqlConnection|ExecuteSqlRaw)\b|HttpMethod\.(Put|Post|Delete|Patch)\b");
        foreach (var file in core) forbidden.Matches(Code(file)).Select(m => m.Value).Should().BeEmpty($"{Path.GetFileName(file)} may only send through the one sender and read evidence");
        Code(Path.Combine(api, "Controllers", "ActiveEventsController.cs")).Should().NotMatchRegex(@"\[FromBody\]\s*(string|JsonElement|JsonDocument|byte\[\])", "no endpoint accepts a raw payload");
    }

    private sealed class ExampleVerifier(IAuthenticatedReviewGateway gateway, IDownstreamVerificationIdentitySource identities)
        : GatewayReadDownstreamVerifier(gateway, identities, TimeProvider.System, TimeSpan.FromMilliseconds(1))
    {
        protected override string Source => "Example read (fake gateway)";
        protected override bool Applies(ActiveEventScenarioDescriptor scenario) => scenario.ExtensionId == "example";
        protected override DownstreamReadContract? Contract(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario, ActiveEventCorrelation correlation, out string reason)
        {
            reason = "";
            return new($"https://utdanning-dev.example.test/records/{correlation.ExpectedResourceIdentity}", correlation.ExpectedResourceIdentity!);
        }
    }

    private sealed class FixedIdentity(bool available = true) : IDownstreamVerificationIdentitySource
    {
        public AuthenticatedReviewIdentity? Resolve(ActiveEventTrustedTarget target, out string reason)
        {
            reason = available ? "" : "No machine identity token source is configured.";
            return available ? new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "profile", "fingerprint") : null;
        }
    }

    private sealed class FakeGateway : IAuthenticatedReviewGateway
    {
        public int Status { get; init; } = 200;
        public bool Executed { get; init; } = true;
        public List<(string Method, string Url)> Calls { get; } = [];
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default)
        {
            Calls.Add((httpMethod, url));
            return Task.FromResult(Executed
                ? new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.Executed, Result = new AuthenticatedApiExecutionResult { StatusCode = Status } }
                : new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.NoContext, Message = "No authenticated context (fake)." });
        }
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
