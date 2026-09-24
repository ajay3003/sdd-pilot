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
    Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken cancellationToken);
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
            Attended = AttendedReadiness(request),
            ElementPick = PickBlockedReason(request.ProfileId, request.EnvironmentType) is { } reason
                ? new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession, Message = reason }
                : new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Select an element in the paired browser." },
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

        // Readiness is a statement about the browser right now. Stored evidence contributes nothing to it: a page we
        // captured a DOM from yesterday is not a page a command can be delivered to today.
        var live = companion.Status(request.ProfileId).Live;
        if (!live.ExtensionConnected)
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.RequiresBrowserSession,
                Message = "The Browser Companion is not connected.",
                Action = "Open the application in your normal browser, sign in, and pair the Browser Companion.",
            };
        if (live.LiveApprovedPageCount == 0)
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.RequiresBrowserSession,
                Message = "The Browser Companion is connected, but no approved application page is open.",
                Action = "Open a signed-in page of the application in the paired browser.",
            };
        if (live.CurrentPage is null)
            // Choosing one for the user would mean a run silently drove whichever tab registered first.
            return new CriticalE2EEngineStatus
            {
                State = CriticalE2EEngineState.RequiresBrowserSession,
                Message = $"{live.LiveApprovedPageCount} approved pages are open.",
                Action = "Leave one application page open, or select the page the run should use.",
            };

        return new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = $"Ready on {live.CurrentPage.Identity}." };
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

    /// <summary>
    /// Authoring: let the tester pick one element on the live page and return its identity. Same prerequisites as a
    /// browser run — non-production, connected, exactly one live approved page — plus a companion build that can pick.
    /// Every missing prerequisite is its own Blocked reason; stored evidence never stands in for the live page.
    /// </summary>
    /// <summary>What the content script reports when nobody clicked in time (content.js).</summary>
    public const string PickTimeoutPrefix = "No element was selected before the picker timed out";
    private const string PickUnsupportedReason = "The paired Browser Companion does not support element picking. Reload the extension to update it.";

    /// <summary>
    /// The one rule for whether attended browser steps can reach a page right now: non-production, connected, and exactly
    /// one approved page. Element picking and the readiness summary both read it.
    /// </summary>
    private string? AttendedBlockedReason(string? profileId, string? environmentType)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return "No Target Environment is selected.";
        if (!CriticalE2EEnvironmentPolicy.AllowsAutomation(environmentType)) return CriticalE2EEnvironmentPolicy.BlockedReason(environmentType);
        var live = companion.Status(profileId).Live;
        if (!live.ExtensionConnected) return "The Browser Companion is not connected.";
        if (live.LiveApprovedPageCount == 0) return "No approved application page is open in the paired browser.";
        if (live.CurrentPage is null) return $"{live.LiveApprovedPageCount} approved application pages are open. Keep exactly one approved target page open.";
        return null;
    }

    /// <summary>The one rule for whether picking can start: the overview shows it and the pick itself enforces it.</summary>
    private string? PickBlockedReason(string? profileId, string? environmentType) =>
        AttendedBlockedReason(profileId, environmentType)
        ?? (companion.Status(profileId!).Live.SupportsElementPick ? null : PickUnsupportedReason);

    private CriticalE2EAttendedReadiness AttendedReadiness(CriticalE2EOverviewRequest request)
    {
        var live = string.IsNullOrWhiteSpace(request.ProfileId) ? BrowserCompanionLiveSession.Disconnected : companion.Status(request.ProfileId).Live;
        var reason = AttendedBlockedReason(request.ProfileId, request.EnvironmentType);
        return new CriticalE2EAttendedReadiness
        {
            Status = reason is null
                ? new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Ready for attended browser steps." }
                : new CriticalE2EEngineStatus
                {
                    State = CriticalE2EEnvironmentPolicy.AllowsAutomation(request.EnvironmentType) ? CriticalE2EEngineState.RequiresBrowserSession : CriticalE2EEngineState.Unavailable,
                    Message = reason,
                },
            CompanionConnected = live.ExtensionConnected,
            OpenApprovedPages = live.LiveApprovedPageCount,
            CurrentOrigin = live.CurrentOrigin,
            CurrentRoute = live.CurrentRoute,
            ElementPickSupported = live.SupportsElementPick,
        };
    }

    public async Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken cancellationToken)
    {
        CriticalE2EElementPickResult Blocked(string message, CriticalE2EPickOutcome outcome = CriticalE2EPickOutcome.Blocked) =>
            new() { Status = CriticalE2EStatus.Blocked, Outcome = outcome, Message = message };
        if (PickBlockedReason(request.ProfileId, request.EnvironmentType) is { } reason)
            return Blocked(reason, reason == PickUnsupportedReason ? CriticalE2EPickOutcome.Unsupported : CriticalE2EPickOutcome.Blocked);
        var live = companion.Status(request.ProfileId).Live;

        // The companion polls quickly while this window is open, so the pick mode starts within a heartbeat or so.
        companion.OpenAutomationWindow(request.ProfileId);
        var commandId = $"pick:{Guid.NewGuid():N}";
        var dispatch = companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = commandId,
            StepId = "pick",
            ProfileId = request.ProfileId,
            EnvironmentId = request.EnvironmentId,
            TargetOrigin = live.CurrentOrigin!,
            PageId = live.CurrentPageId,
            Action = CompanionActionKind.PickElement,
            TimeoutMs = Math.Clamp(request.TimeoutMs, 5_000, 60_000),
        });
        if (!dispatch.Accepted) return Blocked(dispatch.Message);

        CompanionAutomationResult result;
        try { result = await companion.AwaitResultAsync(commandId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            companion.CancelCommand(commandId, "Element picking was cancelled.");
            return new CriticalE2EElementPickResult { Status = CriticalE2EStatus.Cancelled, Outcome = CriticalE2EPickOutcome.Cancelled, Message = "Element picking was cancelled." };
        }

        logger.LogInformation("Critical E2E element pick {CommandId}: {Status}", commandId, result.Status);
        return result switch
        {
            { Status: CriticalE2EStatus.Passed, Element: { } element } => new CriticalE2EElementPickResult
            {
                Status = CriticalE2EStatus.Passed, Outcome = CriticalE2EPickOutcome.Picked, Element = element,
                Message = element.Recommended is null
                    ? "Element picked, but no selector identifies it uniquely."
                    : $"Element picked: {element.Recommended.Describe()}.",
            },
            { Status: CriticalE2EStatus.Cancelled } => new CriticalE2EElementPickResult { Status = CriticalE2EStatus.Cancelled, Outcome = CriticalE2EPickOutcome.Cancelled, Message = result.SanitizedError ?? "Selection cancelled." },
            // The page's own timeout wording (content.js). An expiry here means the page never answered at all.
            { SanitizedError: { } error } when error.StartsWith(PickTimeoutPrefix, StringComparison.Ordinal) => Blocked(error, CriticalE2EPickOutcome.TimedOut),
            { Status: CriticalE2EStatus.Failed } => Blocked(result.SanitizedError ?? "Element picking failed.", CriticalE2EPickOutcome.Failed),
            _ => Blocked(result.SanitizedError ?? "The Browser Companion did not return an element."),
        };
    }

    public async Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        var selected = string.IsNullOrWhiteSpace(request.FlowId)
            // A batch runs critical flows. Diagnostic flows run one at a time, on purpose, never as part of the regression.
            ? store.Flows(context.EnvironmentId).Where(f => f.Enabled && f.Kind == CriticalE2EFlowKind.Critical && (request.Mode is null || f.Mode == request.Mode)).ToList()
            : [.. new[] { store.Flow(request.FlowId!) }.Where(f => f is not null).Cast<CriticalE2EFlowDefinition>()];

        if (selected.Count == 0)
            return new CriticalE2ERunBatchResult { Overview = Overview(context), Message = "No matching flows to run." };

        // The origin browser steps act on comes from the paired session, not from the flow: a flow definition must not
        // be able to point the authenticated browser at an origin the user never approved.
        var live = companion.Status(context.ProfileId).Live;
        var runs = new List<CriticalE2ERunResult>();
        foreach (var flow in selected)
        {
            var result = await runner.RunAsync(new CriticalE2ERunRequest
            {
                Flow = flow,
                ApiIdentity = Identity(context),
                TargetOrigin = live.CurrentOrigin,
                PageId = live.CurrentPageId,
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
