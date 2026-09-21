using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Integration steps. The distinction that carries the most weight here is the one between "the API said no" and "we
/// could not reach the API as ourselves": the first is a finding about the system, the second is a finding about our own
/// setup, and a runner that silently falls back to an anonymous request turns "this endpoint is protected" into a pass.
/// </summary>
public sealed class IntegrationStepExecutorTests
{
    private sealed class FakeGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Capabilities { get; set; } = new() { AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true };
        public Queue<AuthenticatedReviewExecutionOutcome> Outcomes { get; } = new();
        public List<string> RestCalls { get; } = [];
        public List<string> GraphQlCalls { get; } = [];

        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => Capabilities;

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken ct = default)
        {
            RestCalls.Add($"{httpMethod} {url}");
            return Task.FromResult(Next());
        }

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken ct = default)
        {
            GraphQlCalls.Add(query);
            return Task.FromResult(Next());
        }

        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken ct = default) =>
            Task.FromResult(new AuthenticatedGraphQlSchemaOutcome());

        private AuthenticatedReviewExecutionOutcome Next() => Outcomes.Count > 0 ? Outcomes.Dequeue() : Executed(200);
    }

    private static AuthenticatedReviewExecutionOutcome Executed(int status, bool? hasData = null, int? errors = null) => new()
    {
        Status = AuthenticatedExecutionStatus.Executed,
        Result = new AuthenticatedApiExecutionResult { StatusCode = status, GraphQlHasData = hasData, GraphQlErrorCount = errors },
    };

    private static readonly AuthenticatedReviewIdentity Identity = new(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "fingerprint");

    private static CriticalE2ERunContext Context() => new()
    {
        Flow = new CriticalE2EFlowDefinition { Id = "flow-1", ProfileId = "dev", EnvironmentId = "env-1" },
        RunId = "run-1", CorrelationId = "M2LB-E2E-20260921-abc", StartedAt = DateTimeOffset.UnixEpoch,
        ApiIdentity = Identity,
    };

    private static async Task<CriticalE2EStepResult> RunAsync(FakeGateway gateway, CriticalE2EStepDefinition step, CriticalE2ERunContext? context = null) =>
        await new IntegrationStepExecutor(gateway, TimeProvider.System, NullLogger<IntegrationStepExecutor>.Instance)
            .ExecuteAsync(step, context ?? Context(), CancellationToken.None);

    private static CriticalE2EStepDefinition Http(CriticalE2EExpectation? expect = null) => new()
    {
        StepId = "s1", IntegrationAction = CriticalE2EIntegrationKind.Http, Method = "GET",
        PathOrOperation = "https://api.example.test/health", IsFinalAssertion = true,
        Expect = expect ?? new CriticalE2EExpectation { Observable = CriticalE2EObservable.StatusCode, ExpectedValue = "200" },
    };

    [Fact]
    public async Task AnHttpStepPassesWhenTheExpectedStatusCameBack()
    {
        var gateway = new FakeGateway();
        gateway.Outcomes.Enqueue(Executed(200));
        var result = await RunAsync(gateway, Http());
        result.Status.Should().Be(CriticalE2EStatus.Passed);
        gateway.RestCalls.Should().ContainSingle().Which.Should().Be("GET https://api.example.test/health");
    }

    [Fact]
    public async Task AWrongStatusIsAFailureAndSaysWhatWasExpectedAndWhatArrived()
    {
        var gateway = new FakeGateway();
        gateway.Outcomes.Enqueue(Executed(503));
        var result = await RunAsync(gateway, Http());
        result.Status.Should().Be(CriticalE2EStatus.Failed);
        result.SanitizedError.Should().Contain("StatusCode").And.Contain("200").And.Contain("503");
    }

    [Fact]
    public async Task NoAuthenticatedContextBlocksTheStepRatherThanTryingAnonymously()
    {
        var gateway = new FakeGateway { Capabilities = new AuthenticatedReviewCapabilities { AuthenticatedApi = false, Reason = "Start the local proxy and sign in." } };
        var result = await RunAsync(gateway, Http());
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().Contain("Start the local proxy");
        gateway.RestCalls.Should().BeEmpty("falling back to an anonymous request would turn a protected API into a pass");
    }

    [Fact]
    public async Task AnIdentityThatWasNeverConfiguredBlocksTheStep()
    {
        var result = await RunAsync(new FakeGateway(), Http(), Context() with { ApiIdentity = null });
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().Contain("No authenticated API context");
    }

    [Fact]
    public async Task ARequestTheGatewayRefusedToExecuteIsBlockedNotFailed()
    {
        var gateway = new FakeGateway();
        gateway.Outcomes.Enqueue(new AuthenticatedReviewExecutionOutcome
        {
            Status = AuthenticatedExecutionStatus.Expired,
            Message = "Authenticated API session expired.",
        });
        var result = await RunAsync(gateway, Http());
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().Contain("expired");
    }

    [Fact]
    public async Task APollKeepsAskingUntilTheExpectationHolds()
    {
        var gateway = new FakeGateway();
        // The record has not propagated yet, twice, and then it has. This is the shape of every event-driven flow.
        gateway.Outcomes.Enqueue(Executed(200, hasData: false));
        gateway.Outcomes.Enqueue(Executed(200, hasData: false));
        gateway.Outcomes.Enqueue(Executed(200, hasData: true));

        var result = await RunAsync(gateway, new CriticalE2EStepDefinition
        {
            StepId = "poll", IntegrationAction = CriticalE2EIntegrationKind.Poll, PathOrOperation = "https://api.example.test/graphql",
            Body = "query { person(correlationId: \"${CorrelationId}\") { id } }", IsFinalAssertion = true,
            TimeoutMs = 10_000, PollIntervalMs = 200,
            Expect = new CriticalE2EExpectation { Observable = CriticalE2EObservable.GraphQlHasData, ExpectedValue = "True" },
        });

        result.Status.Should().Be(CriticalE2EStatus.Passed);
        gateway.GraphQlCalls.Should().HaveCount(3);
        result.SafeSummary.Should().Contain("3 polls");
    }

    [Fact]
    public async Task APollThatNeverSucceedsFailsWithinItsTimeoutRatherThanRunningForever()
    {
        var gateway = new FakeGateway();
        for (var i = 0; i < 50; i++) gateway.Outcomes.Enqueue(Executed(200, hasData: false));

        var result = await RunAsync(gateway, new CriticalE2EStepDefinition
        {
            StepId = "poll", IntegrationAction = CriticalE2EIntegrationKind.Poll, PathOrOperation = "https://api.example.test/graphql",
            Body = "query { x }", IsFinalAssertion = true, TimeoutMs = 700, PollIntervalMs = 200,
            Expect = new CriticalE2EExpectation { Observable = CriticalE2EObservable.GraphQlHasData, ExpectedValue = "True" },
        });

        result.Status.Should().Be(CriticalE2EStatus.Failed, "the API answered every time; the business state never arrived");
        gateway.GraphQlCalls.Should().HaveCountLessThan(10);
    }

    [Fact]
    public async Task TheCorrelationIdIsSubstitutedIntoTheQuerySoTheFlowCanFindItsOwnRecord()
    {
        var gateway = new FakeGateway();
        gateway.Outcomes.Enqueue(Executed(200, hasData: true));
        await RunAsync(gateway, new CriticalE2EStepDefinition
        {
            StepId = "q", IntegrationAction = CriticalE2EIntegrationKind.GraphQl, PathOrOperation = "https://api.example.test/graphql",
            Body = "query { person(correlationId: \"${CorrelationId}\") { id } }", IsFinalAssertion = true,
            Expect = new CriticalE2EExpectation { Observable = CriticalE2EObservable.GraphQlHasData, ExpectedValue = "True" },
        });

        gateway.GraphQlCalls.Single().Should().Contain("M2LB-E2E-20260921-abc").And.NotContain("${CorrelationId}");
    }

    [Fact]
    public async Task AGraphQlErrorCountExpectationDistinguishesTransportSuccessFromABusinessAnswer()
    {
        var gateway = new FakeGateway();
        // HTTP 200 with GraphQL errors: the transport worked and the operation did not.
        gateway.Outcomes.Enqueue(Executed(200, hasData: false, errors: 2));
        var result = await RunAsync(gateway, new CriticalE2EStepDefinition
        {
            StepId = "q", IntegrationAction = CriticalE2EIntegrationKind.GraphQl, PathOrOperation = "https://api.example.test/graphql",
            Body = "query { x }", IsFinalAssertion = true,
            Expect = new CriticalE2EExpectation { Observable = CriticalE2EObservable.GraphQlErrorCount, ExpectedValue = "0" },
        });
        result.Status.Should().Be(CriticalE2EStatus.Failed);
        result.ObservedValue.Should().Be("2");
    }

    [Fact]
    public async Task AStepWithNoEndpointIsBlockedBeforeAnyRequestIsMade()
    {
        var gateway = new FakeGateway();
        var result = await RunAsync(gateway, Http() with { PathOrOperation = "" });
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        gateway.RestCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task EventHubIsRefusedWithItsReasonRatherThanGuessingANamespace()
    {
        var executor = new EventHubStepExecutor(TimeProvider.System);
        var step = new CriticalE2EStepDefinition { StepId = "publish", IntegrationAction = CriticalE2EIntegrationKind.EventHub };
        executor.CanExecute(step).Should().BeTrue();
        var result = await executor.ExecuteAsync(step, Context(), CancellationToken.None);
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().Contain("not available in this version");
    }

    [Fact]
    public void TheIntegrationExecutorDoesNotClaimBrowserSteps()
    {
        var executor = new IntegrationStepExecutor(new FakeGateway(), TimeProvider.System, NullLogger<IntegrationStepExecutor>.Instance);
        executor.CanExecute(new CriticalE2EStepDefinition { BrowserAction = CompanionActionKind.Click }).Should().BeFalse();
        executor.CanExecute(new CriticalE2EStepDefinition { IntegrationAction = CriticalE2EIntegrationKind.EventHub }).Should().BeFalse();
    }
}
