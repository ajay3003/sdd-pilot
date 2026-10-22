using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.Schemas;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SchemaEvidence;

/// <summary>A contract evidence provider: one format family, its capabilities (detection ≠ parsing ≠ semantic structure ≠ comparison ≠ test
/// generation; runtime validation always separate) and what it cannot prove. Providers parse locally; nothing is fetched.</summary>
public interface IContractEvidenceProvider
{
    ContractEvidenceProviderDescriptor Descriptor { get; }
}

/// <summary>The shared compiled XSD engine as a provider: package files in, the normalized contract shape out — the same shape Source Analysis
/// stores, so Schema Studio, Test Data and Impact Analysis compare and query one model.</summary>
public sealed class XsdPackageContractProvider : IContractEvidenceProvider
{
    public ContractEvidenceProviderDescriptor Descriptor => ContractEvidenceProviders.Get("xsd-package");

    public SourceContract Read(string name, IReadOnlyDictionary<string, string> files)
    {
        var analysis = XmlSchemaPackageAnalyzer.Analyze(files);
        return XmlSchemaContractProjection.FromPackage(name, analysis.Files, analysis.EffectiveSchema, analysis.Identity);
    }
}

/// <summary>An OpenAPI 3 document as normalized contract evidence: operations, request/response types and the document's info.version (its
/// own meaning — never the application or deployment version). Security schemes are declared metadata, never verified authentication.</summary>
public sealed class OpenApiContractProvider : IContractEvidenceProvider
{
    public ContractEvidenceProviderDescriptor Descriptor => ContractEvidenceProviders.Get("openapi");

    public static SourceContract FromNormalized(string name, string file, NormalizedContract contract, string? infoVersion = null) => new()
    {
        Id = $"openapi:{file}", Type = SourceContractType.OpenApi, Name = name, Version = infoVersion, File = file, Line = 1, Format = "json",
        Operations = [.. contract.Operations.Select(o => new ContractOperation($"{o.Method} {o.Path}", o.Method, o.Path, [.. o.Parameters.Select(p => p.Name)]))],
        ParseSupport = DomainSupport.Supported,
    };
}

/// <summary>Stored Source Analysis contract evidence (all formats it reads), extracted once at upload and never re-parsed by consumers.</summary>
public sealed class SourceSnapshotContractProvider : IContractEvidenceProvider
{
    public ContractEvidenceProviderDescriptor Descriptor => ContractEvidenceProviders.Get("xsd-source");

    public static ContractEvidenceSource? Source(IqrSourceSnapshot snapshot) =>
        snapshot.EvidenceDomains?.Contracts is { } evidence ? new(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, evidence) : null;
}

public sealed record ContractEvidenceProviderStatus(ContractEvidenceProviderDescriptor Descriptor, string Detail);

/// <summary>One contract in a snapshot as consumers read it over HTTP: meaning first, provenance kept.</summary>
public sealed record ContractFactView(string Id, string Name, string Format, string File, string Usage, string Parse, string EntryPoint,
    IReadOnlyList<ContractVersionFact> Versions, IReadOnlyList<ContractReferenceFact> UnresolvedReferences, int Operations, int Fields, int Constraints, ContractProvenance Provenance);

/// <summary>A snapshot's contract evidence: summary, contracts and version groups. Evidence older than the contract analyzer's current version
/// is shown as stored — never re-parsed or upgraded in place.</summary>
public sealed record ContractEvidenceView(Guid SnapshotId, string Archive, DateTimeOffset AnalyzedAt, int AnalyzerVersion, ContractSummary Summary,
    IReadOnlyList<ContractFactView> Contracts, IReadOnlyList<string> VersionGroups, string? Limitation);

/// <summary>Read-only queries over stored contract evidence for one exact snapshot (never the newest in its place).</summary>
public sealed class ContractEvidenceService(IReviewSourceEvidenceProvider sources)
{
    public static IReadOnlyList<ContractEvidenceProviderStatus> Providers() =>
        [.. ContractEvidenceProviders.All.Select(d => new ContractEvidenceProviderStatus(d, d.Key switch
        {
            "graphql-introspection" => "Runtime evidence in API Quality Review; not part of source contract evidence.",
            "test-pack-reference" => "Reference metadata from integration templates; never a source snapshot.",
            _ => "Local and offline; parsed once per immutable source snapshot or package.",
        }))];

    public async Task<ContractEvidenceView?> GetAsync(string environmentId, Guid snapshotId, CancellationToken ct)
    {
        if (await sources.ResolveAsync(environmentId, snapshotId, ct) is not { } snapshot) return null;
        if (SourceSnapshotContractProvider.Source(snapshot) is not { } source)
            return new(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, 0, new(0, [], 0, 0, 0, 0, 0, 0), [], [],
                "Contract semantic evidence is unavailable in this analysis version. Analyze the archive again in Source Analysis (a new snapshot); this one is not changed.");
        var facts = ContractEvidenceQueries.GetContracts(source);
        return new(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, source.Evidence.AnalyzerVersion, ContractEvidenceQueries.Summary(source),
            [.. facts.Select(f => new ContractFactView(f.Id, f.Name, ContractEvidenceQueries.Label(f.Contract.Type), f.Contract.File, ContractEvidenceQueries.Label(f.Usage),
                ContractEvidenceQueries.Label(f.Parse), f.EntryPoint.Text, f.Versions, f.UnresolvedReferences, f.Contract.Operations.Count,
                ContractEvidenceQueries.GetFields(f).Count, ContractEvidenceQueries.GetConstraints(f).Count, f.Provenance))],
            [.. ContractEvidenceQueries.VersionGroups(facts).Select(g => $"{g.Members[0].Name}: {g.Text}")], null);
    }

    /// <summary>Constraints of one contract in one snapshot — the rules Test Data and generated tests consume, with provenance.</summary>
    public async Task<IReadOnlyList<ContractConstraintFact>?> ConstraintsAsync(string environmentId, Guid snapshotId, string contractId, CancellationToken ct)
    {
        if (await sources.ResolveAsync(environmentId, snapshotId, ct) is not { } snapshot || SourceSnapshotContractProvider.Source(snapshot) is not { } source) return null;
        return source.Evidence.Contracts.FirstOrDefault(c => c.Id == contractId) is { } contract
            ? ContractEvidenceQueries.GetConstraints(ContractEvidenceQueries.Fact(source, contract)) : null;
    }

    /// <summary>One contract compared between two exact immutable snapshots with the shared comparison engine (the one Schema Studio and
    /// Impact Analysis use). Each snapshot keeps its own evidence; nothing is merged.</summary>
    public async Task<IReadOnlyList<ContractChangeFact>?> CompareAsync(string environmentId, Guid before, Guid after, string contractId, CancellationToken ct)
    {
        var a = await sources.ResolveAsync(environmentId, before, ct);
        var b = await sources.ResolveAsync(environmentId, after, ct);
        var old = a?.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(c => c.Id == contractId);
        var current = b?.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(c => c.Id == contractId);
        return old is null || current is null ? null : ContractComparison.Compare(old, current);
    }
}
