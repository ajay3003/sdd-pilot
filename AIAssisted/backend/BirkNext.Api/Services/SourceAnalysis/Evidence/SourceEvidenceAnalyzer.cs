using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.SourceObservability;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>One source-evidence domain analyzer. It reads the shared context and sets ITS domain result; it never interprets for a review.</summary>
internal interface ISourceEvidenceDomainAnalyzer
{
    DomainAnalyzerInfo Info { get; }
    void Analyze(SourceEvidenceContext context, CancellationToken ct);
    /// <summary>The domain result when the analyzer failed (FailedAnalysis is reserved for this).</summary>
    void Failed(SourceEvidenceContext context, string reason);
}

/// <summary>
/// The evidence-domain registry: analyzers declare their domain, technologies, version, stage and the domains they depend on; the order is
/// derived from those declarations (never from registration order), and a cycle is a configuration error. Stage 1 extracts and normalizes
/// each domain from the snapshot; stage 3 cross-links domains; stage 4 builds the summaries (file roles, capability matrix).
/// </summary>
internal sealed class SourceEvidenceAnalyzerRegistry(IEnumerable<ISourceEvidenceDomainAnalyzer> analyzers)
{
    public IReadOnlyList<ISourceEvidenceDomainAnalyzer> Ordered { get; } = Order(analyzers.ToList());

    public static SourceEvidenceAnalyzerRegistry Default { get; } = new([new InfrastructureAnalyzer(), new ConfigurationAnalyzer(), new PipelineAnalyzer(), new ContractAnalyzer(), new CrossDomainLinker()]);

    private static List<ISourceEvidenceDomainAnalyzer> Order(List<ISourceEvidenceDomainAnalyzer> all)
    {
        var byDomain = all.ToDictionary(a => a.Info.Domain);
        var ordered = new List<ISourceEvidenceDomainAnalyzer>();
        var state = new Dictionary<SourceEvidenceDomain, int>();
        void Visit(ISourceEvidenceDomainAnalyzer a, Stack<SourceEvidenceDomain> path)
        {
            if (state.TryGetValue(a.Info.Domain, out var s))
            {
                if (s == 1) throw new InvalidOperationException($"Source evidence analyzers have a dependency cycle: {string.Join(" → ", path.Reverse().Append(a.Info.Domain))}.");
                return;
            }
            state[a.Info.Domain] = 1;
            path.Push(a.Info.Domain);
            foreach (var dependency in a.Info.DependsOn)
            {
                if (!byDomain.TryGetValue(dependency, out var d)) throw new InvalidOperationException($"{a.Info.Name} depends on {dependency}, which has no registered analyzer.");
                if (d.Info.Stage > a.Info.Stage) throw new InvalidOperationException($"{a.Info.Name} (stage {a.Info.Stage}) cannot depend on a later stage ({d.Info.Name}, stage {d.Info.Stage}).");
                Visit(d, path);
            }
            path.Pop();
            state[a.Info.Domain] = 2;
            ordered.Add(a);
        }
        foreach (var a in all.OrderBy(a => a.Info.Stage).ThenBy(a => a.Info.Domain)) Visit(a, new Stack<SourceEvidenceDomain>());
        return ordered;
    }
}

/// <summary>
/// Source Analysis → reusable source-evidence domains: Infrastructure as Code, Configuration, CI/CD, Contracts and cross-domain links,
/// analysed ONCE at upload over the same immutable workspace as Architecture, Database and Observability (no second upload, no rescan on read).
/// The result is bound to the snapshot id and fingerprint and versioned per analyzer; a newer analyzer version only affects new analyses.
/// </summary>
public static class SourceEvidenceAnalyzer
{
    public const int Version = 1;

    public static SourceEvidenceDomainsSnapshot Analyze(Guid sourceSnapshotId, IqrSourceArchiveReader.Workspace workspace, DateTimeOffset at, ArchitectureSnapshot? architecture,
        DatabaseArchitectureSnapshot? database, SourceObservabilitySnapshot? observability, IntegrationPathEvidence? integrationPath, CancellationToken ct = default) =>
        Analyze(sourceSnapshotId, workspace, ArchitectureInput.From(sourceSnapshotId, workspace), at, architecture, database, observability, integrationPath, ct, out _);

    internal static SourceEvidenceDomainsSnapshot Analyze(Guid sourceSnapshotId, IqrSourceArchiveReader.Workspace workspace, ArchitectureInput input, DateTimeOffset at,
        ArchitectureSnapshot? architecture, DatabaseArchitectureSnapshot? database, SourceObservabilitySnapshot? observability, IntegrationPathEvidence? integrationPath,
        CancellationToken ct, out SourceConfigurationModel configuration, SourceEvidenceAnalyzerRegistry? registry = null)
    {
        registry ??= SourceEvidenceAnalyzerRegistry.Default;
        var files = workspace.Files.Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? []).GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First())
            .Select(f => { var (role, technology) = SourceFileClassifier.Classify(f.Path, f.Content); return new EvidenceFile(f.Path, f.Content, role, technology); })
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var context = new SourceEvidenceContext
        {
            SourceSnapshotId = sourceSnapshotId, Fingerprint = workspace.Archive.Sha256, ExtractedAt = at, Files = files, Input = input,
            Architecture = architecture, Database = database, Observability = observability, IntegrationPath = integrationPath,
        };
        foreach (var analyzer in registry.Ordered)
        {
            ct.ThrowIfCancellationRequested();
            try { analyzer.Analyze(context, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failing analyzer marks only its own domain as FailedAnalysis; the exception text is not stored (it could quote source).
                analyzer.Failed(context, $"{analyzer.Info.Name} failed on this snapshot ({ex.GetType().Name}); other domains are unaffected.");
            }
        }
        configuration = context.ConfigurationModel;
        return new SourceEvidenceDomainsSnapshot
        {
            SourceSnapshotId = sourceSnapshotId, SourceFingerprint = workspace.Archive.Sha256, Version = Version, ExtractedAt = at,
            Analyzers = registry.Ordered.Select(a => a.Info).ToList(),
            Capabilities = context.Capabilities.DistinctBy(c => (c.Domain, c.Technology)).OrderBy(c => c.Domain).ThenBy(c => c.Technology, StringComparer.Ordinal).ToList(),
            FileRoles = files.GroupBy(f => f.Role).OrderBy(g => g.Key).Select(g => new SourceFileRoleSummary(g.Key, g.Count(), g.Take(5).Select(f => SourceEvidenceRedaction.Safe(f.Path)).ToList())).ToList(),
            Infrastructure = context.Infrastructure ?? context.Envelope(new InfrastructureEvidence(), SourceEvidenceDomain.Infrastructure, 0),
            Configuration = context.Configuration ?? context.Envelope(new ConfigurationEvidence(), SourceEvidenceDomain.Configuration, 0),
            CiCd = context.CiCd ?? context.Envelope(new PipelineEvidence(), SourceEvidenceDomain.CiCd, 0),
            Contracts = context.Contracts ?? context.Envelope(new ContractEvidence(), SourceEvidenceDomain.Contracts, 0),
            CrossDomain = context.CrossDomain ?? context.Envelope(new CrossDomainEvidence(), SourceEvidenceDomain.CrossDomain, 0),
        };
    }

    internal static DomainAnalyzerInfo Info(SourceEvidenceDomain domain, string name, int version, int stage, string[] technologies, SourceEvidenceDomain[] dependsOn, string[] produces) =>
        new(domain, name, version, stage, [.. technologies], [.. dependsOn], [.. produces]);
}
