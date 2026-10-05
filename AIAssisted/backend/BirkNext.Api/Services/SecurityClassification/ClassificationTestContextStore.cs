using System.Collections.Concurrent;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.SecurityClassification;

/// <summary>
/// The temporary security test context, held in process memory only: never written to the database, a file, a log or a review run. Keyed by
/// caller scope (the authenticated principal, or "local" for the single-user local app) and Target Environment, so a DEV context is never
/// used for QA. Lost when the backend restarts; <see cref="Clear"/> removes it. Registered as a singleton.
/// </summary>
public sealed class ClassificationTestContextStore
{
    private readonly ConcurrentDictionary<(string Scope, string EnvironmentId), ClassificationTestContext> _contexts = new();

    public ClassificationTestContext? Get(string scope, string environmentId) => _contexts.TryGetValue((scope, environmentId), out var context) ? context : null;

    public void Set(string scope, string environmentId, ClassificationTestContext context) => _contexts[(scope, environmentId)] = context;

    public bool Clear(string scope, string environmentId) => _contexts.TryRemove((scope, environmentId), out _);

    /// <summary>Local data reset: removes every temporary test context.</summary>
    public int ClearAll() { var n = _contexts.Count; _contexts.Clear(); return n; }
}
