using System.Text.RegularExpressions;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Everything a step needs that is not in its own definition. Carried through the run so a step never reaches back into
/// global state to find out which environment it is running against.
/// </summary>
public sealed record CriticalE2ERunContext
{
    public required CriticalE2EFlowDefinition Flow { get; init; }
    public required string RunId { get; init; }
    public required string CorrelationId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public AuthenticatedReviewIdentity? ApiIdentity { get; init; }
    public string? TargetOrigin { get; init; }
    /// <summary>
    /// The live page every browser step of this run is bound to. Changes only when the run's own Navigate reloaded the
    /// bound tab: the executor then rebinds to that tab's new page, never to another tab.
    /// </summary>
    public string? PageId { get; set; }
    /// <summary>Values produced by earlier steps, addressable as <c>${step:&lt;stepId&gt;}</c>.</summary>
    public Dictionary<string, string> Outputs { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One transport's way of performing a step. Browser steps and integration steps have nothing in common at the wire
/// level and everything in common at the result level, so the seam is here rather than in the runner.
/// </summary>
public interface ICriticalE2EStepExecutor
{
    bool CanExecute(CriticalE2EStepDefinition step);
    Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The only substitution a flow gets. Not an expression language: four run-scoped values plus earlier steps' outputs.
/// A test definition that can compute is a test definition nobody can review.
/// </summary>
public static class CriticalE2EVariables
{
    private static readonly Regex Token = new(@"\$\{([A-Za-z0-9_:.-]{1,64})\}", RegexOptions.Compiled);

    public static string? Resolve(string? template, CriticalE2ERunContext context, DateTimeOffset now) =>
        template is null ? null : Token.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (name.StartsWith("step:", StringComparison.OrdinalIgnoreCase))
                return context.Outputs.TryGetValue(name[5..], out var output) ? output : match.Value;
            return name.ToLowerInvariant() switch
            {
                "runid" => context.RunId,
                "correlationid" => context.CorrelationId,
                "timestamp" => now.ToString("yyyyMMddHHmmss"),
                "randomguid" => Guid.NewGuid().ToString("N")[..12],
                // An unknown token stays literal. Silently substituting an empty string is how a flow ends up
                // searching for "" and passing.
                _ => match.Value,
            };
        });
}

/// <summary>Shared result shaping, so every executor reports Blocked and Failed the same way.</summary>
public static class CriticalE2EStepOutcome
{
    public static CriticalE2EStepResult From(CriticalE2EStepDefinition step, DateTimeOffset startedAt, DateTimeOffset completedAt,
        CriticalE2EStatus status, string? summary = null, string? error = null, string? route = null, string? value = null,
        string? evidence = null, BrowserEvidenceFreshness? freshness = null) =>
        new()
        {
            StepId = step.StepId,
            Description = string.IsNullOrWhiteSpace(step.Description) ? Describe(step) : step.Description,
            Status = status,
            IsFinalAssertion = step.IsFinalAssertion,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (completedAt - startedAt).TotalMilliseconds,
            SafeSummary = summary,
            SanitizedError = error,
            ObservedRoute = route,
            ObservedValue = value,
            EvidenceReference = evidence,
            EvidenceFreshness = freshness,
        };

    /// <summary>A readable line for a step whose author did not write one. Never the raw definition dumped into the UI.</summary>
    public static string Describe(CriticalE2EStepDefinition step) => step switch
    {
        { BrowserAction: { } action, Selector: { } selector } => $"{action} {selector.Describe()}",
        { BrowserAction: { } action } => $"{action} {step.Expected ?? step.Value ?? ""}".TrimEnd(),
        { IntegrationAction: { } action } => $"{action} {step.Method} {step.PathOrOperation}".Trim(),
        _ => step.StepId,
    };
}
