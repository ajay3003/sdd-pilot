using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.BuildInfo;

// BirkNext's OWN software build identity (application version, CI build, commit), read from the metadata compiled into each component by
// versioning/BirkNext.BuildIdentity.targets. Separate from the versions of artifacts BirkNext manages (contracts, tests, Test Data) and from
// the build of an application under test. Model: docs/architecture/birknext-build-version-model.md.

/// <summary>Local: a developer build. CI: an Azure DevOps run with a resolved identity. Release: an official release run.
/// Unknown: no identity could be established (e.g. a CI environment that skipped the resolver, or a build without the targets).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildChannel { Unknown, Local, CI, Release }

/// <summary>Complete: CI/Release with run id and commit. Partial: a local build with a known commit (no pipeline). Unknown: no commit.
/// Inconsistent: the compiled values contradict each other (or a packaged manifest).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProvenanceStatus { Unknown, Partial, Complete, Inconsistent }

/// <summary>Frontend vs backend. One release package ships both from the same build, so only Matched or Mismatch are claimed;
/// CompatibleDifferentBuilds exists for independently deployed components but no rule produces it today (none is defined).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComponentCompatibility { NotAssessed, Matched, CompatibleDifferentBuilds, Mismatch }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManifestCheckStatus { NotPackaged, Consistent, Inconsistent, Invalid }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SchemaCompatibility { Unknown, Current, PendingMigrations, AheadOfBuild }

public sealed record BuildIdentity
{
    public string Component { get; init; } = "";
    public string Product { get; init; } = "BirkNext";
    public string ApplicationVersion { get; init; } = "";
    public string BuildVersion { get; init; } = "";
    public string InformationalVersion { get; init; } = "";
    public BuildChannel Channel { get; init; }
    public string? BuildId { get; init; }
    public string? BuildNumber { get; init; }
    /// <summary>The full 40-character commit, or null when unknown (never a guess).</summary>
    public string? SourceCommit { get; init; }
    public string? SourceBranch { get; init; }
    /// <summary>True when built from a working tree with uncommitted changes; null when unknown.</summary>
    public bool? SourceDirty { get; init; }
    public string? BuildTimestampUtc { get; init; }
    public string? Configuration { get; init; }
    public string? PipelineDefinition { get; init; }
    public string AssemblyVersion { get; init; } = "";
    public string? FileVersion { get; init; }
    public ProvenanceStatus Provenance { get; init; }
    public List<string> Problems { get; init; } = [];

    [JsonIgnore] public string? ShortCommit => SourceCommit is { Length: >= 12 } c ? c[..12] : SourceCommit;
}

public static class BuildIdentityReader
{
    public const string Schema = "birknext-build-identity/1";
    private static readonly Regex Sha = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    /// <summary>The identity compiled into <paramref name="assembly"/>. Missing values stay null; contradictions are listed in Problems.</summary>
    public static BuildIdentity FromAssembly(Assembly assembly, string component)
    {
        var meta = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(a => a.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value is { Length: > 0 } v ? v : null, StringComparer.Ordinal);
        string? M(string key) => meta.TryGetValue(key, out var v) ? v : null;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "";
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var problems = new List<string>();
        if (M("BirkNextIdentitySchema") != Schema)
        {
            return new()
            {
                Component = component, InformationalVersion = informational, BuildVersion = informational.Split('+')[0], ApplicationVersion = informational.Split('+', '-')[0],
                AssemblyVersion = assemblyVersion, FileVersion = fileVersion, Channel = BuildChannel.Unknown, Provenance = ProvenanceStatus.Unknown,
                Problems = ["This component carries no BirkNext build identity (built without versioning/BirkNext.BuildIdentity.targets)."],
            };
        }
        var channel = Enum.TryParse<BuildChannel>(M("BirkNextBuildChannel"), true, out var c) ? c : BuildChannel.Unknown;
        var commitText = M("SourceCommit");
        var commit = commitText is { } t && Sha.IsMatch(t) ? t : null;
        if (commitText is { } bad && bad != "unknown" && commit is null) problems.Add($"The recorded source commit '{bad}' is not a full commit SHA.");
        bool? dirty = M("BirkNextSourceDirty") switch { "true" => true, "false" => false, _ => null };
        var buildVersion = M("BirkNextBuildVersion") ?? "";
        var expected = $"{buildVersion}+{commitText ?? "unknown"}{(dirty == true ? ".dirty" : "")}";
        if (informational != expected) problems.Add($"The informational version '{informational}' does not match the build identity '{expected}'.");
        var buildId = M("BirkNextBuildId");
        if (channel is BuildChannel.CI or BuildChannel.Release && (buildId is null || commit is null)) problems.Add($"A {channel} build without a pipeline run id and a full commit.");
        var provenance = problems.Count > 0 ? ProvenanceStatus.Inconsistent
            : channel is BuildChannel.CI or BuildChannel.Release ? ProvenanceStatus.Complete
            : channel == BuildChannel.Local && commit is not null ? ProvenanceStatus.Partial
            : ProvenanceStatus.Unknown;
        return new()
        {
            Component = component, ApplicationVersion = buildVersion.Split('-')[0], BuildVersion = buildVersion, InformationalVersion = informational, Channel = channel,
            BuildId = buildId, BuildNumber = M("BirkNextBuildNumber"), SourceCommit = commit, SourceBranch = M("BirkNextSourceBranch"), SourceDirty = dirty,
            BuildTimestampUtc = M("BirkNextBuildTimestamp"), Configuration = M("BirkNextBuildConfiguration"), PipelineDefinition = M("BirkNextPipelineDefinition"),
            AssemblyVersion = assemblyVersion, FileVersion = fileVersion, Provenance = provenance, Problems = problems,
        };
    }
}

public static class BuildCompatibilityRules
{
    /// <summary>
    /// Frontend vs backend of one release package. Matched: the same build (informational version, channel and run id). Mismatch: both
    /// identities are known and differ — a frontend from another build than the backend it talks to. NotAssessed: an identity is missing, a
    /// commit is unknown, or either was built from uncommitted changes (the same commit then does not prove the same source).
    /// </summary>
    public static (ComponentCompatibility Status, string Explanation) Compare(BuildIdentity frontend, BuildIdentity backend)
    {
        if (frontend.Provenance is ProvenanceStatus.Unknown || backend.Provenance is ProvenanceStatus.Unknown || frontend.SourceCommit is null || backend.SourceCommit is null)
            return (ComponentCompatibility.NotAssessed, "Not assessed: a component has no known source commit.");
        if (frontend.Provenance == ProvenanceStatus.Inconsistent || backend.Provenance == ProvenanceStatus.Inconsistent)
            return (ComponentCompatibility.NotAssessed, "Not assessed: a component's build identity is inconsistent.");
        if (frontend.SourceDirty == true || backend.SourceDirty == true)
            return (ComponentCompatibility.NotAssessed, "Not assessed: built from uncommitted changes, so the same commit does not prove the same source.");
        if (frontend.InformationalVersion == backend.InformationalVersion && frontend.Channel == backend.Channel && frontend.BuildId == backend.BuildId)
            return (ComponentCompatibility.Matched, $"Frontend and backend are the same build ({backend.InformationalVersion}).");
        return (ComponentCompatibility.Mismatch, $"Frontend {frontend.InformationalVersion} and backend {backend.InformationalVersion} are different builds; a release package ships both from one build.");
    }
}

/// <summary>The packaged build-manifest.json compared with the running backend (NotPackaged when running from a source build).</summary>
public sealed record ManifestCheck(ManifestCheckStatus Status, string? BuildVersion, string? Channel, string? Fingerprint, List<string> Differences);

public sealed record ConnectedCompanion(string Version, string? BuildId, int Sessions);

/// <summary>
/// The Browser Companion's own version (not BirkNext's). Bundled: the build packaged with this backend. Connected: what paired browsers report.
/// The Companion has no protocol-version handshake, so compatibility is NotAssessed; the note says whether a connected Companion is the
/// bundled build.
/// </summary>
public sealed record CompanionBuildInfo
{
    public string? BundledVersion { get; init; }
    public string? BundledBuildId { get; init; }
    public List<ConnectedCompanion> Connected { get; init; } = [];
    public ComponentCompatibility Compatibility { get; init; } = ComponentCompatibility.NotAssessed;
    public string Note { get; init; } = "";
}

public sealed record SchemaState(string Context, int Known, int Applied, int Pending, string? LatestApplied, SchemaCompatibility Status, string Note);

public sealed record BuildInfoResponse
{
    public BuildIdentity Backend { get; init; } = new();
    public ManifestCheck Manifest { get; init; } = new(ManifestCheckStatus.NotPackaged, null, null, null, []);
    public CompanionBuildInfo Companion { get; init; } = new();
    public List<SchemaState> Database { get; init; } = [];
    public DateTimeOffset EvaluatedAt { get; init; }
}
