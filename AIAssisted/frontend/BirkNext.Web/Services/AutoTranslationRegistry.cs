using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Automatic translation in Planlegg og klargjør (2026-11-25): one request per plan revision for the whole browser session. A request still running
/// is shared when the page is reopened or the tab changes (never sent twice), and its outcome is remembered per revision so a failure is shown again
/// with «Prøv igjen» instead of being retried silently on every visit. Registered once per app (singleton); a page without it keeps its own.
/// </summary>
public sealed class AutoTranslationRegistry
{
    private readonly Dictionary<Guid, (int Revision, Task<(TranslateScenarioResult? Result, string? Error)> Call)> _running = [];
    private readonly Dictionary<string, string?> _outcome = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private static string Key(Guid plan, int revision) => $"{plan:N}:{revision}";

    /// <summary>The call already running for this plan revision, or a new one from <paramref name="start"/>. Null when this revision was already
    /// handled (its outcome is in <see cref="Outcome"/>) and <paramref name="retry"/> is false.</summary>
    public Task<(TranslateScenarioResult? Result, string? Error)>? Begin(Guid plan, int revision, bool retry,
        Func<Task<(TranslateScenarioResult? Result, string? Error)>> start)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(plan, out var running) && running.Revision == revision) return running.Call;
            if (!retry && _outcome.ContainsKey(Key(plan, revision))) return null;
            _outcome.Remove(Key(plan, revision));
            var call = start();
            _running[plan] = (revision, call);
            return call;
        }
    }

    /// <summary>Records how the request for this revision ended (null error = done) and releases it.</summary>
    public void End(Guid plan, int revision, Task<(TranslateScenarioResult? Result, string? Error)> call, string? error)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(plan, out var running) && running.Call == call) _running.Remove(plan);
            _outcome[Key(plan, revision)] = error;
        }
    }

    /// <summary>Whether this revision was handled, and the error it ended with (null = translated or nothing missing).</summary>
    public (bool Handled, string? Error) Outcome(Guid plan, int revision)
    {
        lock (_gate) return _outcome.TryGetValue(Key(plan, revision), out var error) ? (true, error) : (false, null);
    }
}
