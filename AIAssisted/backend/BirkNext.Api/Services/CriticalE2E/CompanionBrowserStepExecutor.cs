using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Executes browser steps through the Browser Companion: queue one typed command against the paired session, wait for
/// the page's answer, translate it into a step result.
///
/// This executor has no idea how a click happens, and that is the point. Playwright and CDP are absent from the whole
/// path — the page is driven by a content script the browser itself runs, in a session the user authenticated by hand.
/// </summary>
public sealed class CompanionBrowserStepExecutor(IBrowserCompanionService companion, TimeProvider time, ILogger<CompanionBrowserStepExecutor> logger)
    : ICriticalE2EStepExecutor
{
    public bool CanExecute(CriticalE2EStepDefinition step) => step.IsBrowserStep;

    /// <summary>
    /// How current the evidence this step points at actually is. Without this, a result could reference a page whose
    /// last capture was yesterday and read exactly like one backed by a snapshot taken during the run.
    /// </summary>
    private BrowserEvidenceFreshness? Freshness(CriticalE2ERunContext context, string? evidenceReference)
    {
        if (string.IsNullOrWhiteSpace(evidenceReference)) return null;
        var status = companion.Status(context.Flow.ProfileId);
        if (status.Pages.FirstOrDefault(p => string.Equals(p.Identity, evidenceReference, StringComparison.OrdinalIgnoreCase)) is not { } page)
            return null;
        return BrowserEvidenceFreshnessPolicy.Classify(page.CapturedAt, status.PairedAt, context.StartedAt,
            page.Identity, context.TargetOrigin is null ? null : evidenceReference);
    }

    /// <summary>Waits for the bound tab's reloaded page; returns why not when it does not come back.</summary>
    private async Task<string?> RebindAfterNavigationAsync(CriticalE2ERunContext context, int timeoutMs, CancellationToken cancellationToken)
    {
        var before = companion.Status(context.Flow.ProfileId).Live.LivePages.FirstOrDefault(p => p.PageId == context.PageId);
        var tab = before?.TabKey ?? (context.PageId is { } id && id.LastIndexOf('-') is > 0 and var dash ? id[..dash] : null);
        if (tab is null) return null;
        // Bounded by attempts, not wall-clock arithmetic, so a paused clock cannot make this wait forever.
        var attempts = Math.Clamp(timeoutMs, 2_000, 60_000) / 250;
        for (var i = 0; i < attempts; i++)
        {
            var successor = companion.Status(context.Flow.ProfileId).Live.LivePages.FirstOrDefault(p =>
                p.TabKey == tab && p.PageId != context.PageId
                && string.Equals(p.Origin, context.TargetOrigin, StringComparison.OrdinalIgnoreCase));
            if (successor is not null)
            {
                logger.LogInformation("Critical E2E run {RunId} followed its navigation to {PageId}", context.RunId, successor.PageId);
                context.PageId = successor.PageId;
                return null;
            }
            // Still the same instance: the navigation has not unloaded the page yet, or it was same-document.
            if (i > 0 && companion.Status(context.Flow.ProfileId).Live.LivePages.Any(p => p.PageId == context.PageId) && i >= attempts / 2)
                return null;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        return "The page did not come back after navigating; the step's tab no longer has an approved page.";
    }

    public async Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        CriticalE2EStepResult Blocked(string reason) => CriticalE2EStepOutcome.From(step, startedAt, time.GetUtcNow(), CriticalE2EStatus.Blocked, error: reason);

        if (string.IsNullOrWhiteSpace(context.TargetOrigin))
            return Blocked("No approved application origin is configured for this environment.");

        var commandId = $"{context.RunId}:{step.StepId}";
        var command = new CompanionAutomationCommand
        {
            CommandId = commandId,
            RunId = context.RunId,
            FlowId = context.Flow.Id,
            StepId = step.StepId,
            ProfileId = context.Flow.ProfileId,
            EnvironmentId = context.Flow.EnvironmentId,
            TargetOrigin = context.TargetOrigin,
            // Every step of the run goes to the page the run was bound to, never to whichever page is open by now.
            PageId = context.PageId,
            Action = step.BrowserAction!.Value,
            Selector = step.Selector,
            Value = CriticalE2EVariables.Resolve(step.Value, context, startedAt),
            Expected = CriticalE2EVariables.Resolve(step.Expected, context, startedAt),
            Match = step.Match,
            TimeoutMs = step.TimeoutMs,
        };

        var dispatch = companion.Dispatch(command);
        // A refusal to queue is always a prerequisite problem — no page, wrong environment, companion gone. It is never
        // evidence about the application, so it must never be recorded as a failing flow.
        if (!dispatch.Accepted) return Blocked(dispatch.Message);

        CompanionAutomationResult result;
        try
        {
            result = await companion.AwaitResultAsync(commandId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            companion.CancelCommand(commandId, "The run was cancelled.");
            return CriticalE2EStepOutcome.From(step, startedAt, time.GetUtcNow(), CriticalE2EStatus.Cancelled, error: "Cancelled.");
        }

        logger.LogInformation("Critical E2E step {StepId} of run {RunId}: {Status}", step.StepId, context.RunId, result.Status);

        // Navigating by route is a full page load in the bound tab, so the page the run was bound to ends by design and
        // the tab's next content script replaces it. Follow that tab — only after the run's own Navigate, only the same
        // tab and origin. Any other reload still makes the next step Blocked as stale.
        if (step.BrowserAction == CompanionActionKind.Navigate && step.Selector is null && result.Status == CriticalE2EStatus.Passed
            && await RebindAfterNavigationAsync(context, step.TimeoutMs, cancellationToken).ConfigureAwait(false) is { } lost)
            return CriticalE2EStepOutcome.From(step, startedAt, time.GetUtcNow(), CriticalE2EStatus.Blocked,
                summary: result.SafeSummary, error: lost, route: result.ObservedRoute);
        if (!string.IsNullOrWhiteSpace(result.ObservedValue)) context.Outputs[step.StepId] = result.ObservedValue;

        return CriticalE2EStepOutcome.From(step, startedAt, time.GetUtcNow(), result.Status,
            summary: result.SafeSummary, error: result.SanitizedError, route: result.ObservedRoute,
            value: result.ObservedValue, evidence: result.EvidenceReference,
            freshness: Freshness(context, result.EvidenceReference));
    }
}
