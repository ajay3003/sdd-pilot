using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.BrowserCompanion;

/// <summary>
/// The BirkNext → extension direction of the companion channel: typed browser commands for Critical E2E.
///
/// There is no externally callable automation endpoint. A command is queued against an existing paired session and
/// handed out on the extension's own next heartbeat, so it inherits the pairing's proof — session id, extension origin,
/// profile and approved origins — instead of introducing a second thing to authenticate. The extension asks; BirkNext
/// never reaches into the browser.
///
/// The state machine exists for one reason: a heartbeat that is retried, or a worker that restarts mid-flight, must not
/// produce a second click. A command leaves <see cref="CompanionCommandState.Pending"/> exactly once.
/// </summary>
public sealed partial class BrowserCompanionService
{
    /// <summary>How long a queued command stays executable. A click the user has since navigated away from is not a click we want.</summary>
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(45);

    private sealed class PendingCommand
    {
        public required CompanionAutomationCommand Command { get; init; }
        public required DateTimeOffset QueuedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public CompanionCommandState State { get; set; } = CompanionCommandState.Pending;
        public CompanionAutomationResult? Result { get; set; }
        public TaskCompletionSource<CompanionAutomationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Terminal => State is CompanionCommandState.Passed or CompanionCommandState.Failed or CompanionCommandState.Blocked
            or CompanionCommandState.Expired or CompanionCommandState.Cancelled;
    }

    /// <summary>Commands are found by id across sessions so a result envelope does not have to re-state which session it belongs to.</summary>
    private readonly Dictionary<string, string> _commandOwners = new(StringComparer.Ordinal);

    /// <summary>How long the companion keeps polling quickly after BirkNext last showed interest in running something.</summary>
    public static readonly TimeSpan AutomationWindow = TimeSpan.FromMinutes(3);

    public void OpenAutomationWindow(string profileId)
    {
        lock (_gate)
        {
            if (_sessionsByProfile.TryGetValue(profileId, out var session))
                session.AutomationWindowUntil = time.GetUtcNow() + AutomationWindow;
        }
    }

    public CompanionCommandDispatchResult Dispatch(CompanionAutomationCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId)) return Refuse("", "A command id is required.");
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_commandOwners.ContainsKey(command.CommandId))
                return Refuse(command.CommandId, "That command id has already been used. Command ids are single-use.");

            if (!_sessionsByProfile.TryGetValue(command.ProfileId, out var session) || session.Expired(now))
                return Refuse(command.CommandId, "No Browser Companion session for this environment. Pair the companion and sign in.");

            // Production is refused here, again in the worker and again in the page. One layer is a preference; three is a rule.
            if (!CriticalE2EEnvironmentPolicy.AllowsAutomation(session.EnvironmentType))
                return Refuse(command.CommandId, CriticalE2EEnvironmentPolicy.BlockedReason(session.EnvironmentType));

            var origin = ApplicationPagePolicy.CanonicalOrigin(command.TargetOrigin);
            if (origin is null || !session.ApprovedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                return Refuse(command.CommandId, "The command targets an origin this session has not approved.");

            // Bind to a LIVE page. Stored evidence never resolves a target: a command must go to a page that exists
            // now, and with several open the caller has to say which one rather than have one chosen for it.
            ExpireLivePages(session, now);
            var target = command.PageId is { Length: > 0 } requested
                ? session.LivePages.GetValueOrDefault(requested)
                : session.CurrentPage;
            if (target is null)
                return Refuse(command.CommandId, session.LivePages.Count switch
                {
                    0 => "No approved page is open in the paired browser.",
                    _ when command.PageId is { Length: > 0 } => "The page this command was bound to is no longer open.",
                    _ => $"{session.LivePages.Count} approved pages are open; the run must name which one to use.",
                });
            if (!string.Equals(target.Origin, origin, StringComparison.OrdinalIgnoreCase))
                return Refuse(command.CommandId, "The live page is not on the origin this command targets.");

            // Picking needs a build that can do it; an older extension would answer "unsupported action" only after
            // the tester had already been told to click.
            if (command.Action == CompanionActionKind.PickElement && !session.Capabilities.Contains(CompanionCapabilities.ElementPick))
                return Refuse(command.CommandId, "The paired Browser Companion does not support element picking. Reload the extension to update it.");

            // One step at a time. A flow is a sequence, and two commands in flight would make "which click produced this
            // route" unanswerable.
            ExpireCommands(session, now);
            if (session.Commands.Values.FirstOrDefault(c => !c.Terminal) is { } busy)
                return Refuse(command.CommandId, $"A browser command is already in flight for this environment ({busy.State}).");

            var timeout = Math.Clamp(command.TimeoutMs, 500, 60_000);
            var pending = new PendingCommand
            {
                // The command carries the page and the content-script instance it was bound to, so every layer below
                // can check it is still acting on the same page rather than on whatever is open by the time it lands.
                Command = command with { TargetOrigin = origin, TimeoutMs = timeout, PageId = target.PageId, ContentScriptInstanceId = target.ContentScriptInstanceId },
                QueuedAt = now,
                // A pick waits for a person, so it lives as long as the tester is given plus delivery slack. Every other
                // command keeps the short lifetime that stops a stale click from ever executing.
                ExpiresAt = now + (command.Action == CompanionActionKind.PickElement
                    ? TimeSpan.FromMilliseconds(timeout) + CommandLifetime
                    : CommandLifetime),
            };
            session.Commands[command.CommandId] = pending;
            _commandOwners[command.CommandId] = session.ProfileId;
            logger.LogInformation("Critical E2E command {CommandId} queued for environment {ProfileId} ({Action})",
                command.CommandId, session.ProfileId, command.Action);
            return new CompanionCommandDispatchResult { Accepted = true, CommandId = command.CommandId, State = CompanionCommandState.Pending, Message = "Queued." };
        }
    }

    /// <summary>
    /// Hands the session's one pending command to the extension. Called from inside the heartbeat, under the same lock,
    /// after the session has already been proven. A command is returned exactly once; every later heartbeat sees nothing,
    /// which is what makes a retried heartbeat harmless.
    /// </summary>
    private CompanionAutomationCommand? ClaimNextCommand(Session session, DateTimeOffset now)
    {
        ExpireCommands(session, now);
        if (session.Commands.Values.FirstOrDefault(c => c.State == CompanionCommandState.Pending) is not { } pending) return null;
        pending.State = CompanionCommandState.Claimed;
        logger.LogInformation("Critical E2E command {CommandId} claimed by the companion", pending.Command.CommandId);
        return pending.Command;
    }

    public BrowserCompanionAcceptResult CompleteCommand(CompanionAutomationResultEnvelope envelope, string extensionOrigin)
    {
        lock (_gate)
        {
            var session = Resolve(envelope.SessionId, envelope.ProfileId, extensionOrigin, out var reason);
            if (session is null) return new BrowserCompanionAcceptResult { Accepted = false, Message = reason };
            session.LastSeenAt = time.GetUtcNow();

            var result = envelope.Result;
            if (!session.Commands.TryGetValue(result.CommandId ?? "", out var pending))
                return new BrowserCompanionAcceptResult { Accepted = false, Message = "Unknown command for this session." };

            // A replayed result is not an error and must not overwrite the outcome that was already recorded: the first
            // answer is the one that describes what the page actually did.
            if (pending.Terminal)
                return new BrowserCompanionAcceptResult { Accepted = true, Message = $"Command already {pending.State}." };

            var sanitized = Sanitize(result);
            pending.Result = sanitized;
            pending.State = sanitized.Status switch
            {
                CriticalE2EStatus.Passed => CompanionCommandState.Passed,
                CriticalE2EStatus.Failed => CompanionCommandState.Failed,
                CriticalE2EStatus.Cancelled => CompanionCommandState.Cancelled,
                _ => CompanionCommandState.Blocked,
            };
            pending.Completion.TrySetResult(sanitized);
            logger.LogInformation("Critical E2E command {CommandId} completed: {State}", sanitized.CommandId, pending.State);
            return new BrowserCompanionAcceptResult { Accepted = true, Message = "OK" };
        }
    }

    public async Task<CompanionAutomationResult> AwaitResultAsync(string commandId, CancellationToken cancellationToken)
    {
        PendingCommand? pending;
        lock (_gate) { pending = Find(commandId); }
        if (pending is null) return Outcome(commandId, "", CriticalE2EStatus.Blocked, "The command is no longer queued.");
        if (pending.Result is { } already) return already;

        // The deadline is the command's own, not the caller's: whoever is waiting must not be able to extend how long a
        // click stays live in the browser.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = pending.ExpiresAt - time.GetUtcNow();
        try
        {
            if (remaining > TimeSpan.Zero) deadline.CancelAfter(remaining);
            return await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (pending.Result is { } raced) return raced;
                var cancelled = cancellationToken.IsCancellationRequested;
                pending.State = cancelled ? CompanionCommandState.Cancelled : CompanionCommandState.Expired;
                var outcome = Outcome(commandId, pending.Command.StepId,
                    cancelled ? CriticalE2EStatus.Cancelled : CriticalE2EStatus.Blocked,
                    cancelled ? "Cancelled before the browser reported a result."
                              : "The Browser Companion did not execute the step before it expired. Check that the paired page is still open.");
                pending.Result = outcome;
                pending.Completion.TrySetResult(outcome);
                return outcome;
            }
        }
    }

    public void CancelCommand(string commandId, string reason)
    {
        lock (_gate)
        {
            if (Find(commandId) is not { } pending || pending.Terminal) return;
            pending.State = CompanionCommandState.Cancelled;
            var outcome = Outcome(commandId, pending.Command.StepId, CriticalE2EStatus.Cancelled, reason);
            pending.Result = outcome;
            pending.Completion.TrySetResult(outcome);
        }
    }

    // ── helpers (all called under _gate unless stated) ───────────────────────

    private PendingCommand? Find(string commandId) =>
        _commandOwners.TryGetValue(commandId, out var profileId) && _sessionsByProfile.TryGetValue(profileId, out var session)
        && session.Commands.TryGetValue(commandId, out var pending) ? pending : null;

    private void ExpireCommands(Session session, DateTimeOffset now)
    {
        foreach (var pending in session.Commands.Values)
        {
            if (pending.Terminal || now <= pending.ExpiresAt) continue;
            pending.State = CompanionCommandState.Expired;
            var outcome = Outcome(pending.Command.CommandId, pending.Command.StepId, CriticalE2EStatus.Blocked,
                "The step expired before the Browser Companion executed it.");
            pending.Result = outcome;
            pending.Completion.TrySetResult(outcome);
        }
    }

    private static CompanionCommandDispatchResult Refuse(string commandId, string message) =>
        new() { Accepted = false, CommandId = commandId, State = CompanionCommandState.Blocked, Message = message };

    private static CompanionAutomationResult Outcome(string commandId, string stepId, CriticalE2EStatus status, string message) =>
        new() { CommandId = commandId, StepId = stepId, Status = status, SanitizedError = message };

    /// <summary>
    /// Everything the page reported passes through the same redaction the evidence path uses. The page is the untrusted
    /// side of this channel: a summary or an error message it produced is data, never something BirkNext repeats verbatim.
    /// </summary>
    private CompanionAutomationResult Sanitize(CompanionAutomationResult result) => result with
    {
        ObservedRoute = BrowserCompanionEvidenceSanitizer.NormalizePath(result.ObservedRoute),
        ObservedValue = Text(result.ObservedValue),
        SafeSummary = Text(result.SafeSummary),
        SanitizedError = Text(result.SanitizedError),
        EvidenceReference = Text(result.EvidenceReference),
        Element = result.Element is { } e ? SanitizeElement(e) : null,
    };

    /// <summary>
    /// The descriptor is identity, not content: short names only, capped, redacted, and never more candidates than the
    /// five strategies produce. The page already withholds values; this is the second layer, not the only one.
    /// </summary>
    private CompanionElementDescriptor SanitizeElement(CompanionElementDescriptor e) => e with
    {
        PageOrigin = ApplicationPagePolicy.CanonicalOrigin(e.PageOrigin) ?? "",
        PageRoute = BrowserCompanionEvidenceSanitizer.NormalizePath(e.PageRoute) ?? "/",
        TagName = Cap(Text(e.TagName), 20) ?? "",
        Role = Cap(Text(e.Role), 30),
        AccessibleName = Cap(Text(e.AccessibleName), 80),
        Label = Cap(Text(e.Label), 80),
        TestId = Cap(Text(e.TestId), 80),
        InputType = Cap(Text(e.InputType), 20),
        Href = Cap(Text(e.Href), 200),
        Candidates = e.Candidates.Take(8).Select(c => c with { Selector = SanitizeSelector(c.Selector) }).ToList(),
        Recommended = e.Recommended is { } r ? SanitizeSelector(r) : null,
    };

    private CompanionSelector SanitizeSelector(CompanionSelector s) => s with
    {
        Value = Cap(Text(s.Value), 160) ?? "",
        Role = Cap(Text(s.Role), 30),
        Name = Cap(Text(s.Name), 80),
    };

    private static string? Cap(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : sanitizer.Text(value);
}
