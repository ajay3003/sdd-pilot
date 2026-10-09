using System.Diagnostics;

namespace BirkNext.RealProjectAcceptance;

/// <summary>
/// The run-scoped context shared by every feature: one dataset, one import, one workspace, one source snapshot. Features read the
/// <see cref="Session"/> they were written for (a browser session in BirkNext.Web.PlaywrightTests) and may cache within the run only.
/// </summary>
public sealed class RealProjectAcceptanceContext
{
    public required RealProjectDataset Dataset { get; init; }
    public required DatasetPreparation Preparation { get; init; }
    public AcceptanceMode Mode { get; init; } = AcceptanceMode.Standard;
    public string ArtifactsDirectory { get; init; } = "";
    public string? BirkNextCommit { get; init; }
    /// <summary>Established once by the import feature; every later feature asserts against it.</summary>
    public WorkspaceIdentity Workspace { get; set; } = new();
    /// <summary>The driver features use (browser + backend client). Opaque to the core so the core stays browser-agnostic.</summary>
    public object? Session { get; init; }
    /// <summary>Run-scoped cache (e.g. the Source Analysis snapshot JSON fetched once). Never persisted.</summary>
    public Dictionary<string, object> Shared { get; } = new(StringComparer.Ordinal);
    public bool RuntimeProfileConfigured => Dataset.RuntimeProfile is not null;
    public FeatureExpectation? ExpectationFor(string featureId) => Dataset.Expectations.TryGetValue(featureId, out var e) ? e : null;
    public bool HashMatched => Preparation.Fingerprint is { } f && Dataset.ExpectedSha256 is { Length: > 0 } expected && string.Equals(f.Sha256, expected, StringComparison.OrdinalIgnoreCase);

    public T SessionAs<T>() where T : class => Session as T ?? throw new InvalidOperationException($"This feature needs a {typeof(T).Name} session.");
}

/// <summary>
/// One acceptance feature: a production page/route exercised against the evidence it actually owns. Implementations live in test
/// infrastructure; production code never references this contract.
/// </summary>
public interface IRealProjectAcceptanceFeature
{
    string FeatureId { get; }
    string DisplayName { get; }
    string Area { get; }
    string Route { get; }
    AcceptanceType AcceptanceType { get; }
    /// <summary>The lowest mode that includes this feature (Smoke features also run in Standard and Full).</summary>
    AcceptanceMode MinimumMode { get; }
    /// <summary>Evidence the feature consumes ("documents", "source snapshot", "runtime target").</summary>
    IReadOnlyList<string> RequiredEvidence { get; }
    /// <summary>Feature ids that must have succeeded first (the import, typically). A failed dependency makes this feature Blocked.</summary>
    IReadOnlyList<string> DependsOn { get; }
    /// <summary>Null when the feature can run; otherwise the reason it is NotAvailable (never a silent pass).</summary>
    string? CanRun(RealProjectAcceptanceContext context);
    Task<FeatureAcceptanceResult> ExecuteAsync(RealProjectAcceptanceContext context, CancellationToken cancellationToken);
}

/// <summary>Executes registered features in order, applies dataset expectations and the cross-feature truthfulness rules, and aggregates.</summary>
public sealed class RealProjectAcceptanceRunner(IReadOnlyList<IRealProjectAcceptanceFeature> features, Action<string>? log = null)
{
    public async Task<RealProjectAcceptanceResult> RunAsync(RealProjectAcceptanceContext context, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var dataset = context.Dataset;
        var results = new List<FeatureAcceptanceResult>();
        var runFindings = new List<AcceptanceFinding>();
        var duplicateIds = features.GroupBy(f => f.FeatureId, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0) throw new InvalidOperationException($"Duplicate acceptance feature ids: {string.Join(", ", duplicateIds)}");

        if (context.Preparation.IsReady)
        {
            foreach (var feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (feature.MinimumMode > context.Mode) continue;
                var result = await RunOneAsync(feature, context, results, cancellationToken);
                result = ApplyRules(feature, result, context);
                results.Add(result);
                log?.Invoke($"[{result.Category}] {feature.FeatureId}: {result.ExecutionStatus}/{result.EvidenceState}/{result.DataState} {string.Join(" | ", result.Findings.Where(f => f.Severity != AcceptanceFindingSeverity.Info).Select(f => f.Code))}");
            }
        }
        else runFindings.Add(new(context.Preparation.State == DatasetPreparationState.HashMismatch ? AcceptanceFindingSeverity.Defect : AcceptanceFindingSeverity.Info,
            $"dataset-{context.Preparation.State}", context.Preparation.Message));

        return new RealProjectAcceptanceResult
        {
            DatasetId = dataset.DatasetId, DatasetDisplayName = dataset.DisplayName, Archive = context.Preparation.Fingerprint, HashMatched = context.HashMatched,
            PreparationState = context.Preparation.State, PreparationMessage = context.Preparation.Message, Mode = context.Mode, BirkNextCommit = context.BirkNextCommit,
            StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, Workspace = context.Workspace, RuntimeProfileConfigured = context.RuntimeProfileConfigured,
            ActiveSendAllowed = dataset.RuntimeProfile?.AllowActiveSend == true, Features = results, RunFindings = runFindings,
        };
    }

    private static async Task<FeatureAcceptanceResult> RunOneAsync(IRealProjectAcceptanceFeature feature, RealProjectAcceptanceContext context, List<FeatureAcceptanceResult> done, CancellationToken ct)
    {
        FeatureAcceptanceResult Base() => new()
        {
            FeatureId = feature.FeatureId, DisplayName = feature.DisplayName, Area = feature.Area, Route = feature.Route, AcceptanceType = feature.AcceptanceType,
        };
        var failedDependency = feature.DependsOn.FirstOrDefault(id => done.FirstOrDefault(r => r.FeatureId == id) is not { } d || d.Category is AcceptanceSummaryCategory.Failed or AcceptanceSummaryCategory.Blocked);
        if (failedDependency is not null)
            return Base() with { ExecutionStatus = ExecutionStatus.Blocked, Notes = [$"Blocked: dependency '{failedDependency}' did not complete."] };
        if (feature.CanRun(context) is { } reason)
            return Base() with { ExecutionStatus = ExecutionStatus.NotAvailable, EvidenceState = EvidenceState.NotVerified, Notes = [reason] };
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await feature.ExecuteAsync(context, ct);
            return result with
            {
                FeatureId = feature.FeatureId, DisplayName = feature.DisplayName, Area = feature.Area, Route = feature.Route, AcceptanceType = feature.AcceptanceType, Duration = watch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Base() with
            {
                ExecutionStatus = ExecutionStatus.Failed, Duration = watch.Elapsed,
                Findings = [new(AcceptanceFindingSeverity.Defect, "unhandled-error", $"{ex.GetType().Name}: {FirstLine(ex.Message)}")],
            };
        }
    }

    /// <summary>
    /// Truthfulness rules shared by every feature, plus the dataset's semantic expectations:
    /// Verified needs assessed data; source evidence needs provenance; runtime evidence needs a runtime profile; expected real data
    /// that is absent is a defect; expected mentions must be observed.
    /// </summary>
    public static FeatureAcceptanceResult ApplyRules(IRealProjectAcceptanceFeature feature, FeatureAcceptanceResult result, RealProjectAcceptanceContext context)
    {
        var findings = new List<AcceptanceFinding>(result.Findings);
        if (result.EvidenceState == EvidenceState.Verified && result.DataState is DataState.NotAssessed or DataState.Unsupported)
            findings.Add(new(AcceptanceFindingSeverity.Defect, "false-verified", $"Reported Verified although the data state is {result.DataState}."));
        if (feature.AcceptanceType == AcceptanceType.Source && result.DataState == DataState.RealDataObserved && result.ProvenanceState == ProvenanceState.Missing)
            findings.Add(new(AcceptanceFindingSeverity.Defect, "missing-provenance", "Source-derived results were observed without provenance to the real project evidence."));
        if (feature.AcceptanceType == AcceptanceType.Runtime && !context.RuntimeProfileConfigured && result.EvidenceState is EvidenceState.Verified or EvidenceState.PartiallyVerified)
            findings.Add(new(AcceptanceFindingSeverity.Defect, "runtime-verified-without-runtime", "Runtime evidence was reported as verified although no runtime profile is paired."));

        if (context.ExpectationFor(feature.FeatureId) is { } expectation && result.ExecutionStatus is not (ExecutionStatus.Blocked or ExecutionStatus.Failed))
        {
            switch (expectation.Presence)
            {
                case ExpectedDataPresence.RealData when result.DataState != DataState.RealDataObserved:
                    findings.Add(new(AcceptanceFindingSeverity.Defect, "expected-data-absent",
                        $"The project contains evidence this feature owns ({expectation.Reason ?? "dataset expectation"}), but the feature reported {result.DataState}."));
                    break;
                case ExpectedDataPresence.NotApplicable when result.DataState == DataState.RealDataObserved:
                    findings.Add(new(AcceptanceFindingSeverity.Warning, "unexpected-data", "Expected not applicable, but data was observed (update the dataset expectation if this is correct)."));
                    break;
                case ExpectedDataPresence.RuntimeNotVerified when result.EvidenceState is EvidenceState.Verified && !context.RuntimeProfileConfigured:
                    findings.Add(new(AcceptanceFindingSeverity.Defect, "runtime-verified-without-runtime", "Runtime-only evidence was reported as verified without a runtime profile."));
                    break;
            }
            foreach (var mention in expectation.MustMention)
                if (!result.Facts.Any(f => f.Contains(mention, StringComparison.OrdinalIgnoreCase)))
                    findings.Add(new(AcceptanceFindingSeverity.Defect, "expected-fact-missing", $"Expected the feature to observe '{mention}' in the real project evidence."));
        }
        return result with { Findings = findings };
    }

    private static string FirstLine(string message) => message.Split('\n')[0].Trim() is var line && line.Length > 300 ? line[..300] : line;
}
