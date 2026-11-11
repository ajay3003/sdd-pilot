using System.Text.Json.Serialization;

namespace BirkNext.ExecutionEvidence;

// Execution manifests: what an execution ran against, recorded once when the run is created and never changed. A run's result is
// evidence for exactly the definitions in its manifest. Whether those definitions are still the current ones is evaluated later
// (ExecutionApplicability) and never changes the historical result: a PASS against contract v3 stays a PASS for v3.

public static class ExecutionManifestSchema
{
    public const string Version = "execution-manifest/1";
    public const string ExecutionEvidenceOwner = "execution-evidence";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManifestDependencyKind { Contract, ContractTest, ContractChain, TestData, EndpointBinding, SourceSnapshot, IntegrationSuite, CompositeContext }

/// <summary>
/// Who stated a dependency. <see cref="OwnerResolved"/>: a BirkNext owner built the executed calls from that stored record (for example Contract
/// Testing materialised the call from test revision r3). <see cref="CallerDeclared"/>: the run's caller only named it (a hand-built run, or a
/// caller override such as an explicit base path), so the run is not proof that the stored record was what executed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManifestDependencyOrigin { OwnerResolved, CallerDeclared }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManifestResolution { Resolved, NotFound }

/// <summary>
/// One pinned dependency. <see cref="Ref"/> is the stable reference ("children-api@v3", "contract-test:{id}@r2", "children@v4",
/// "binding:children-api@local-dev", "suite:{key}@r5"); <see cref="ContentFingerprint"/> is the SHA-256 of the stored record's content at run
/// time (null when it could not be resolved).
/// </summary>
public sealed record ManifestDependency
{
    public ManifestDependencyKind Kind { get; init; }
    public string Ref { get; init; } = "";
    public string? Key { get; init; }
    public int? Version { get; init; }
    public string? ContentFingerprint { get; init; }
    public ManifestDependencyOrigin Origin { get; init; }
    public ManifestResolution Resolution { get; init; }
    public string? Detail { get; init; }
}

/// <summary>The environment the run targeted, as BirkNext knew it at run time (authority only; never a credential).</summary>
public sealed record ManifestEnvironment(string EnvironmentId, string? EnvironmentType, string TargetOrigin, string? AuthenticationMode, string? AuthenticationReference);

/// <summary>The BirkNext build that executed the run (assembly informational version and source commit, "unknown" when not stamped).</summary>
public sealed record ManifestBuild(string Version, string SourceCommit);

public sealed record ExecutionManifest
{
    public string SchemaVersion { get; init; } = ExecutionManifestSchema.Version;
    public Guid ManifestId { get; init; }
    public string Owner { get; init; } = "";
    public Guid RunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Initiator { get; init; } = "";
    public ManifestEnvironment Environment { get; init; } = new("", null, "", null, null);
    public ManifestBuild Build { get; init; } = new("unknown", "unknown");
    public List<ManifestDependency> Dependencies { get; init; } = [];
    /// <summary>SHA-256 over the environment and the dependencies (not the run id, time, initiator or build): two runs with the same value ran
    /// against identical definitions in the same environment.</summary>
    public string DependencyFingerprint { get; init; } = "";
}

/// <summary>
/// Whether a historical result is evidence for what is current now. <see cref="CurrentlyApplicable"/>: every pinned dependency is still the
/// current, unchanged record and every definition was resolved by its owner. <see cref="Historical"/>: at least one dependency was superseded,
/// changed or removed — the result stays true for what it ran against and is not evidence for the current definitions.
/// <see cref="Unknown"/>: no manifest (runs from before manifests existed) or a dependency that cannot be verified.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResultApplicability { CurrentlyApplicable, Historical, Unknown }

/// <summary>
/// <see cref="Unchanged"/>: the pinned record is still the current one with the same content. <see cref="Superseded"/>: it still exists, but a
/// newer version or revision is now current. <see cref="Changed"/>: the same reference now has different content (mutable records such as
/// endpoint bindings). <see cref="Removed"/>: the record no longer exists. <see cref="NotVerifiable"/>: unresolved at run time or caller-declared.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyCurrencyState { Unchanged, Superseded, Changed, Removed, NotVerifiable }

public sealed record DependencyCurrency(ManifestDependency Pinned, DependencyCurrencyState State, string? CurrentRef, string? CurrentFingerprint, string Explanation);

public sealed record ExecutionApplicability
{
    public Guid RunId { get; init; }
    public string Owner { get; init; } = "";
    /// <summary>The run's own, unchanged result.</summary>
    public ExecutionState? ResultState { get; init; }
    public ResultApplicability Applicability { get; init; }
    public string Explanation { get; init; } = "";
    public string? DependencyFingerprint { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    public List<DependencyCurrency> Dependencies { get; init; } = [];
}

public static class ExecutionApplicabilityRules
{
    /// <summary>The combined verdict: any superseded/changed/removed dependency makes the result historical; otherwise anything unverifiable
    /// makes it unknown; only an all-unchanged, all-owner-resolved manifest is currently applicable.</summary>
    public static (ResultApplicability Applicability, string Explanation) Combine(IReadOnlyList<DependencyCurrency> dependencies)
    {
        var moved = dependencies.Where(d => d.State is DependencyCurrencyState.Superseded or DependencyCurrencyState.Changed or DependencyCurrencyState.Removed).ToList();
        if (moved.Count > 0)
            return (ResultApplicability.Historical, $"Historical: {string.Join("; ", moved.Take(4).Select(d => d.Explanation))}{(moved.Count > 4 ? $"; and {moved.Count - 4} more" : "")}. The result stays valid for what it ran against.");
        var unknown = dependencies.Where(d => d.State == DependencyCurrencyState.NotVerifiable).ToList();
        if (unknown.Count > 0)
            return (ResultApplicability.Unknown, $"Not verifiable: {string.Join("; ", unknown.Take(4).Select(d => d.Explanation))}.");
        if (dependencies.Count == 0) return (ResultApplicability.Unknown, "The manifest names no stored definition, so currency cannot be established.");
        return (ResultApplicability.CurrentlyApplicable, "Every pinned definition is still the current one, unchanged.");
    }
}
