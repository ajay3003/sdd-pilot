using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.CriticalE2E;

public interface ICriticalE2EService
{
    CriticalE2EOverview Overview(CriticalE2EOverviewRequest request);
    IReadOnlyList<CriticalE2EFlowDefinition> Flows(string environmentId);
    CriticalE2EFlowDefinition SaveFlow(CriticalE2EFlowDefinition flow);
    bool DeleteFlow(string flowId);
    Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The Critical E2E capability: which flows exist, whether each transport can run right now, what the release verdict
/// is, and running flows through the shared runner.
///
/// Readiness is computed, never assumed. "The companion is paired" and "a browser flow can run" are different claims —
/// the second also needs an approved page open and an environment that may be automated — and this is the one place
/// that knows the difference.
/// </summary>
public sealed class CriticalE2EService(
    ICriticalE2EStore store,
    IBrowserCompanionService companion,
    IAuthenticatedReviewGateway gateway,
    CriticalE2ERunner runner,
    TimeProvider time,
    ILogger<CriticalE2EService> logger) : ICriticalE2EService
{
    public IReadOnlyList<CriticalE2EFlowDefinition> Flows(string environmentId) => store.Flows(environmentId);
    public CriticalE2EFlowDefinition SaveFlow(CriticalE2EFlowDefinition flow) => store.Save(flow);
    public bool DeleteFlow(string flowId) => store.Delete(flowId);

    public CriticalE2EOverview Overview(CriticalE2EOverviewRequest request)
    {
        if (request.Modules.Count > 0) store.SetModules(request.EnvironmentId, request.Modules);
        // Opening this surface is the signal that a run may be coming, so the companion starts polling at step speed
        // now rather than after the user has already pressed Run and waited out a heartbeat.
        if (!string.IsNullOrWhiteSpace(request.ProfileId)) companion.OpenAutomationWindow(request.ProfileId);

        var flows = store.Flows(request.EnvironmentId);
        var history = store.History(request.EnvironmentId);
        var modules = store.Modules(request.EnvironmentId);

        return new CriticalE2EOverview
        {
            EnvironmentId = request.EnvironmentId,
            EnvironmentName = request.EnvironmentName,
            Release = CriticalE2ECoverage.Release(flows, history, modules, request.EnvironmentId, request.BuildId, request.ReleaseId),
            Modules = CriticalE2ECoverage.Modules(flows, history, modules, request.BuildId),
            Flows = flows.Select(f => CriticalE2ECoverage.Summarize(f, history, request.BuildId)).ToList(),
            BrowserEngine = BrowserEngine(request, flows),
            IntegrationEngine = IntegrationEngine(request, flows),
            History = history.Take(25).ToList(),
        };
    }

    private CriticalE2EEngineStatus BrowserEngine(CriticalE2EOverviewRequest request, IReadOnlyList<CriticalE2EFlowDefinition> flows)
    {
        if (!flows.Any(f => f.Enabled && f.Mode == CriticalE2EExecutionMode.CompanionBrowser))
            return new CriticalE2EEngineStatus { State = CriticalE2EEngineState.NotConfigured, Message = "No companion browser flows are configured." };

        // Production is not a "fix this" state; it is out of scope by design, and saying so is more useful than an
        // instruction the user cannot follow.
        if (!CriticalE2EEnvironmentPolicy.AllowsAutomation(request.EnvironmentType))
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.Unavailable,
                Message = CriticalE2EEnvironmentPolicy.BlockedReason(request.EnvironmentType),
            };

        var status = companion.Status(request.ProfileId);
        if (status.State != BrowserCompanionState.Connected)
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.RequiresBrowserSession,
                Message = "The Browser Companion is not connected.",
                Action = "Open the application in your normal browser, sign in, and pair the Browser Companion.",
            };
        if (status.CurrentPageOrigin is null)
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.RequiresBrowserSession,
                Message = "The Browser Companion is connected, but no approved application page is open.",
                Action = "Open a signed-in page of the application in the paired browser.",
            };

        return new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = $"Ready on {status.CurrentPageOrigin}." };
    }

    private CriticalE2EEngineStatus IntegrationEngine(CriticalE2EOverviewRequest request, IReadOnlyList<CriticalE2EFlowDefinition> flows)
    {
        if (!flows.Any(f => f.Enabled && f.Mode == CriticalE2EExecutionMode.AutomatedIntegration))
            return new CriticalE2EEngineStatus { State = CriticalE2EEngineState.NotConfigured, Message = "No automated integration flows are configured." };
        if (Identity(request) is not { } identity)
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.NotConfigured,
                Message = "No authenticated API context is configured for this environment.",
                Action = "Configure authenticated testing on the Target Environment.",
            };
        var capabilities = gateway.Resolve(identity);
        return capabilities.AuthenticatedApi
            ? new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Authenticated API access is available." }
            : new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession, Message = capabilities.Reason, Action = "Refresh the authenticated API context." };
    }

    private static AuthenticatedReviewIdentity? Identity(CriticalE2EOverviewRequest request) =>
        Enum.TryParse<AuthenticatedTestingMethod>(request.AuthenticationMethod, ignoreCase: true, out var method)
            ? new AuthenticatedReviewIdentity(method, request.ProfileId, request.ContextFingerprint)
            : null;

    public async Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        var selected = string.IsNullOrWhiteSpace(request.FlowId)
            ? store.Flows(context.EnvironmentId).Where(f => f.Enabled && (request.Mode is null || f.Mode == request.Mode)).ToList()
            : [.. new[] { store.Flow(request.FlowId!) }.Where(f => f is not null).Cast<CriticalE2EFlowDefinition>()];

        if (selected.Count == 0)
            return new CriticalE2ERunBatchResult { Overview = Overview(context), Message = "No matching flows to run." };

        // The origin browser steps act on comes from the paired session, not from the flow: a flow definition must not
        // be able to point the authenticated browser at an origin the user never approved.
        var companionStatus = companion.Status(context.ProfileId);
        var runs = new List<CriticalE2ERunResult>();
        foreach (var flow in selected)
        {
            var result = await runner.RunAsync(new CriticalE2ERunRequest
            {
                Flow = flow,
                ApiIdentity = Identity(context),
                TargetOrigin = companionStatus.CurrentPageOrigin,
                BuildId = context.BuildId,
                ReleaseId = context.ReleaseId,
                CommitSha = context.CommitSha,
            }, cancellationToken).ConfigureAwait(false);
            store.Record(result);
            runs.Add(result);
            // A cancelled run ends the batch: the user asked for it to stop, not for the next flow to start.
            if (result.Status == CriticalE2EStatus.Cancelled) break;
        }

        logger.LogInformation("Critical E2E batch finished: {Passed}/{Total} passed",
            runs.Count(r => r.Status == CriticalE2EStatus.Passed), runs.Count);
        return new CriticalE2ERunBatchResult
        {
            Runs = runs,
            Overview = Overview(context),
            Message = $"{runs.Count(r => r.Status == CriticalE2EStatus.Passed)} of {runs.Count} flow(s) passed.",
        };
    }
}
