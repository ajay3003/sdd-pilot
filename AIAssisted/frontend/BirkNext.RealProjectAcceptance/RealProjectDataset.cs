using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BirkNext.RealProjectAcceptance;

/// <summary>
/// How deep an acceptance run goes. Smoke: import, core source pages and critical navigation. Standard (default): every
/// source-dependent feature panel. Full: Standard plus responsive/accessibility breadth and optional configured runtime checks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcceptanceMode { Smoke, Standard, Full }

/// <summary>Real project/source acceptance (external archive) vs real runtime acceptance (a configured DEV/QA target). Never mixed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcceptanceType { Source, Runtime, Navigation, Diagnostics }

/// <summary>
/// A real project used for acceptance. Nothing here is project-specific: a dataset is an external archive plus optional semantic
/// expectations. The archive is never copied into the repository, embedded as a resource or duplicated into sample data.
/// </summary>
public sealed record RealProjectDataset
{
    public string DatasetId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>Resolved at run time from an environment variable, CLI option or local test configuration; never committed.</summary>
    public string? ArchivePath { get; init; }
    /// <summary>When set, a different archive fails dataset preparation instead of being used silently.</summary>
    public string? ExpectedSha256 { get; init; }
    public string? ProjectNotes { get; init; }
    public IReadOnlyList<string> ExpectedTechnologyHints { get; init; } = [];
    public IReadOnlyList<string> ExpectedArtifactFamilies { get; init; } = [];
    public RealProjectRuntimeProfile? RuntimeProfile { get; init; }
    /// <summary>Semantic expectations per feature id. A feature without an expectation is evaluated on its own truthful states only.</summary>
    public IReadOnlyDictionary<string, FeatureExpectation> Expectations { get; init; } = new Dictionary<string, FeatureExpectation>(StringComparer.Ordinal);
    /// <summary>Stable aggregate baselines that only apply when the archive hash equals <see cref="ExpectedSha256"/> (e.g. diagnostic counts).</summary>
    public IReadOnlyDictionary<string, long> HashBoundBaselines { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Optional pairing with a real runtime target. No secrets: only the Target Environment identity and what may be exercised. Every
/// state-changing category (event/message sends, mutations, destructive calls) is off unless explicitly permitted.
/// </summary>
public sealed record RealProjectRuntimeProfile
{
    public string TargetEnvironmentId { get; init; } = "";
    public string? ExpectedEnvironmentType { get; init; }
    public IReadOnlyList<string> AllowedRuntimeChecks { get; init; } = [];
    public bool AllowActiveSend { get; init; }
    public bool AllowMutations { get; init; }
    public bool AllowExternalNetworkEnrichment { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExpectedDataPresence
{
    /// <summary>No assertion beyond truthful states.</summary>
    Unspecified,
    /// <summary>The project contains evidence this feature owns: it must observe real data (absence is a defect).</summary>
    RealData,
    /// <summary>The feature is expected not to apply to this project.</summary>
    NotApplicable,
    /// <summary>The feature's evidence is runtime-only and no runtime is paired: NotVerified is the correct, successful outcome.</summary>
    RuntimeNotVerified,
}

/// <summary>A semantic expectation (presence, named facts), not exact counts — counts drift with harmless source changes.</summary>
public sealed record FeatureExpectation
{
    public ExpectedDataPresence Presence { get; init; } = ExpectedDataPresence.Unspecified;
    /// <summary>Facts the feature's evidence must mention (technology names, artifact families, contract kinds) — case-insensitive.</summary>
    public IReadOnlyList<string> MustMention { get; init; } = [];
    public string? Reason { get; init; }
}

/// <summary>Supplies datasets. A provider resolves its archive from the environment; it never alters production behaviour.</summary>
public interface IRealProjectDatasetProvider
{
    string DatasetId { get; }
    /// <summary>The environment variable that carries the external archive path.</summary>
    string ArchiveEnvironmentVariable { get; }
    RealProjectDataset Describe(string? archivePath);
}

public sealed record ArchiveFingerprint(string FileName, string Sha256, long SizeBytes, DateTimeOffset MeasuredAt);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatasetPreparationState { Ready, NotConfigured, ArchiveMissing, HashMismatch }

public sealed record DatasetPreparation(RealProjectDataset Dataset, DatasetPreparationState State, ArchiveFingerprint? Fingerprint, string Message)
{
    public bool IsReady => State == DatasetPreparationState.Ready;

    public const string NotConfiguredReason = "External real-project dataset not configured.";

    /// <summary>Fingerprints the external archive before anything is imported. A changed archive is never used silently.</summary>
    public static DatasetPreparation Prepare(RealProjectDataset dataset, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(dataset.ArchivePath))
            return new(dataset, DatasetPreparationState.NotConfigured, null, NotConfiguredReason);
        if (!File.Exists(dataset.ArchivePath))
            return new(dataset, DatasetPreparationState.ArchiveMissing, null, $"External real-project dataset '{dataset.DatasetId}' is configured but the archive file does not exist.");
        using var stream = File.OpenRead(dataset.ArchivePath);
        var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var fingerprint = new ArchiveFingerprint(Path.GetFileName(dataset.ArchivePath), sha, stream.Length, (clock ?? TimeProvider.System).GetUtcNow());
        if (dataset.ExpectedSha256 is { Length: > 0 } expected && !string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase))
            return new(dataset, DatasetPreparationState.HashMismatch, fingerprint,
                $"Archive SHA-256 {sha} does not match the expected {expected.ToLowerInvariant()} for dataset '{dataset.DatasetId}'. The input changed; refusing to run against it.");
        return new(dataset, DatasetPreparationState.Ready, fingerprint, "Dataset ready.");
    }
}

/// <summary>Registry of dataset providers. Adding a project means adding a provider — the harness and features do not change.</summary>
public sealed class RealProjectDatasetRegistry
{
    private readonly Dictionary<string, IRealProjectDatasetProvider> providers = new(StringComparer.OrdinalIgnoreCase);

    public RealProjectDatasetRegistry(IEnumerable<IRealProjectDatasetProvider> providers)
    {
        foreach (var provider in providers)
            if (!this.providers.TryAdd(provider.DatasetId, provider))
                throw new ArgumentException($"Duplicate real-project dataset id '{provider.DatasetId}'.");
    }

    public IReadOnlyCollection<string> DatasetIds => providers.Keys;

    public IRealProjectDatasetProvider Get(string datasetId) =>
        providers.TryGetValue(datasetId, out var provider) ? provider : throw new KeyNotFoundException($"Unknown real-project dataset '{datasetId}'.");

    /// <summary>Resolves the archive path: explicit option first, then the provider's environment variable. Null = not configured.</summary>
    public RealProjectDataset Resolve(string datasetId, string? explicitArchivePath = null, Func<string, string?>? environment = null)
    {
        var provider = Get(datasetId);
        var env = environment ?? Environment.GetEnvironmentVariable;
        var path = !string.IsNullOrWhiteSpace(explicitArchivePath) ? explicitArchivePath : env(provider.ArchiveEnvironmentVariable);
        return provider.Describe(string.IsNullOrWhiteSpace(path) ? null : path);
    }

    public static AcceptanceMode ResolveMode(Func<string, string?>? environment = null) =>
        Enum.TryParse<AcceptanceMode>((environment ?? Environment.GetEnvironmentVariable)("BIRKNEXT_REAL_ACCEPTANCE_MODE"), ignoreCase: true, out var mode) ? mode : AcceptanceMode.Standard;
}
