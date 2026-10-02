using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.TestEvidence;

namespace BirkNext.Api.Services.TestEvidence;

/// <summary>What the user states about a result artifact. A snapshot selected for correlation is not a claim that the results came from it:
/// only <see cref="SourceBindingConfirmed"/> binds the run to that source version.</summary>
public sealed record TestResultImportContext(string? EnvironmentId, Guid? SourceSnapshotId, bool SourceBindingConfirmed, string? BuildReference, string? CommitReference, string? EnvironmentReference);

/// <summary>
/// Validate/preview of an execution-result artifact: provider parse → normalized executions → deterministic correlation against the selected
/// Source Analysis snapshot's test inventory. Stateless: the immutable executions are recorded in the workspace SDD lifecycle by the client
/// (the same persisted store as all other SDD evidence), so there is no second test-evidence store.
/// </summary>
public sealed class TestExecutionImportService(IEnumerable<ITestExecutionEvidenceProvider> providers, IqrSourceStore sources)
{
    public IReadOnlyList<ITestExecutionEvidenceProvider> Providers { get; } = providers.ToList();

    public async Task<TestResultArtifactPreview> PreviewAsync(string fileName, byte[] content, TestResultImportContext context, CancellationToken ct = default)
    {
        var head = content[..Math.Min(content.Length, 4096)];
        var provider = Providers.FirstOrDefault(p => p.CanRead(fileName, head)) ?? Providers.FirstOrDefault(p => fileName.EndsWith(".trx", StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return new TestResultArtifactPreview { Status = TestResultImportStatus.UnsupportedFormat, FileName = TestEvidenceText.FileName(fileName) ?? "", SizeBytes = content.LongLength,
                Error = "No execution-result provider reads this format. Supported: TRX (.trx)." };
        var preview = provider.Read(fileName, content);
        var build = Clean(context.BuildReference);
        var commit = Clean(context.CommitReference);
        preview = preview with
        {
            BuildReference = build, BuildBinding = build is null ? TestSourceBinding.Unknown : TestSourceBinding.Provided,
            CommitReference = commit, CommitBinding = commit is null ? TestSourceBinding.Unknown : TestSourceBinding.Provided,
            EnvironmentReference = Clean(context.EnvironmentReference),
        };
        if (preview.Status != TestResultImportStatus.Valid) return preview;

        var limitations = preview.Limitations.ToList();
        SourceTestInventory? inventory = null;
        if (context.SourceSnapshotId is { } snapshotId && !string.IsNullOrWhiteSpace(context.EnvironmentId))
        {
            var snapshot = await sources.FindSourceAnalysisAsync(context.EnvironmentId!, snapshotId, ct);
            if (snapshot is null) limitations.Add("The selected source snapshot was not found; executions were not correlated to source tests.");
            else if (snapshot.TestInventory is null) limitations.Add("The selected source snapshot was analyzed before source test discovery existed; re-upload the source to correlate tests.");
            else inventory = snapshot.TestInventory;
        }
        else limitations.Add("No source snapshot selected: executions are recorded without source-test correlation or requirement references.");

        if (inventory is null)
        {
            return preview with
            {
                Executions = preview.Executions.Select(e => e with { Correlation = TestCorrelation.NotAssessed }).ToList(),
                SourceBinding = TestSourceBinding.Unknown, Limitations = limitations,
            };
        }
        if (inventory.Status is SourceTestDiscoveryStatus.Partial or SourceTestDiscoveryStatus.Unsupported)
            limitations.AddRange(inventory.Limitations.Where(l => l.Contains("not implemented", StringComparison.Ordinal) || l.Contains("Partial", StringComparison.Ordinal)));
        var correlated = preview.Executions.Select(e => e with { Correlation = TestEvidenceCorrelation.Correlate(e, inventory.Definitions) }).ToList();
        var matchedIds = correlated.Where(e => e.Correlation.State == TestCorrelationState.Confirmed).Select(e => e.Correlation.TestDefinitionId!).ToHashSet(StringComparer.Ordinal);
        if (!context.SourceBindingConfirmed)
            limitations.Add("Source version not established: the results are correlated to test identities in the selected snapshot, but not bound to it. They are not current verification of that source.");
        if (correlated.Any(e => e.Correlation.State == TestCorrelationState.Ambiguous))
            limitations.Add("Some results match more than one source test and are not linked.");
        return preview with
        {
            Executions = correlated, CorrelationSnapshotId = inventory.SnapshotId, CorrelationSnapshotFingerprint = inventory.SnapshotFingerprint,
            CorrelationRepositoryName = inventory.RepositoryName,
            SourceBinding = context.SourceBindingConfirmed ? TestSourceBinding.Provided : TestSourceBinding.Unknown,
            MatchedDefinitions = inventory.Definitions.Where(d => matchedIds.Contains(d.TestDefinitionId)).ToList(),
            Limitations = limitations.Distinct().ToList(),
        };
    }

    public async Task<(SourceTestInventory? Inventory, string? Error)> SourceTestsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default)
    {
        var snapshot = await sources.FindSourceAnalysisAsync(environmentId, snapshotId, ct);
        if (snapshot is null) return (null, "Source snapshot not found.");
        return snapshot.TestInventory is null ? (null, "This snapshot was analyzed before source test discovery existed. Upload the source again to discover tests.") : (snapshot.TestInventory, null);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : TestEvidenceText.Redact(value.Trim(), 200);
}
