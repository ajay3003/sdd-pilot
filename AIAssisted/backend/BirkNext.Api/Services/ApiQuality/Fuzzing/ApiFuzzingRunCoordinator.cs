using System.Collections.Concurrent;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality.Fuzzing;

public sealed class ApiFuzzingRunConflictException(string message) : Exception(message);

/// <summary>
/// Runs at most one safe-fuzzing run at a time in the background, so the UI can poll progress and cancel. Cancelling stops scheduling new
/// cases and cancels the in-flight request; the run ends Partial with its results so far (findings already observed stay valid).
/// Memory only: the frontend keeps run history; the last 20 runs are kept here for polling.
/// </summary>
public sealed class ApiFuzzingRunCoordinator(IServiceScopeFactory scopes, ILogger<ApiFuzzingRunCoordinator> logger)
{
    private const int MaxRetained = 20;
    private sealed class Run
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public ApiFuzzingReport Report { get; set; } = new();
        public Task? Execution { get; set; }
    }

    private readonly ConcurrentDictionary<string, Run> runs = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public ApiFuzzingReport Start(ApiFuzzingRunRequest request, ApiEnvironmentSafetyDecision? safety = null)
    {
        Run run;
        string runId;
        lock (gate)
        {
            if (runs.Values.Any(r => r.Report.Running))
                throw new ApiFuzzingRunConflictException("A safe-fuzzing run is already in progress. Wait for it to finish or cancel it.");
            runId = $"fuzz-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..34];
            run = new Run
            {
                Cancellation = new CancellationTokenSource(),
                Report = new ApiFuzzingReport { RunId = runId, EnvironmentId = request.Review.Environment.EnvironmentId, EnvironmentName = request.Review.Environment.Name, Level = request.Settings.Level, StartedAt = DateTimeOffset.UtcNow, Running = true, Safety = safety ?? new() },
            };
            runs[runId] = run;
            Trim();
        }
        run.Execution = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IApiFuzzingService>();
                run.Report = await service.RunAsync(request, runId, partial => run.Report = partial, run.Cancellation.Token);
            }
            catch (Exception ex)
            {
                logger.LogError("Safe fuzzing run {RunId} failed with {ExceptionType}.", runId, ex.GetType().Name);
                run.Report = run.Report with { Running = false, CompletedAt = DateTimeOffset.UtcNow, Completeness = ApiFuzzCompleteness.Failed, CompletenessReason = $"The fuzzing run could not complete ({ex.GetType().Name})." };
            }
        });
        return run.Report;
    }

    public ApiFuzzingReport? Get(string runId) => runs.TryGetValue(runId, out var run) ? run.Report : null;

    public ApiFuzzingReport? Cancel(string runId)
    {
        if (!runs.TryGetValue(runId, out var run)) return null;
        if (run.Report.Running) run.Cancellation.Cancel();
        return run.Report;
    }

    /// <summary>For tests: wait until the run has finished.</summary>
    internal Task WaitAsync(string runId) => runs.TryGetValue(runId, out var run) && run.Execution is { } task ? task : Task.CompletedTask;

    private void Trim()
    {
        foreach (var old in runs.Where(r => !r.Value.Report.Running).OrderByDescending(r => r.Value.Report.StartedAt).Skip(MaxRetained).Select(r => r.Key).ToList())
            if (runs.TryRemove(old, out var removed)) removed.Cancellation.Dispose();
    }
}
