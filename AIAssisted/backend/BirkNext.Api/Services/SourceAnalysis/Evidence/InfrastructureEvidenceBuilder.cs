using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// The shared, normalized Infrastructure evidence ONE snapshot's providers write into (Terraform, declarative manifests, infrastructure
/// scripts). Providers add facts with provenance; the Infrastructure analyzer turns the builder into the stored <see cref="InfrastructureEvidence"/>.
/// No provider reads another provider's files, and no consumer reads files at all.
/// </summary>
internal sealed class InfrastructureEvidenceBuilder
{
    public List<InfrastructureResource> Resources { get; } = [];
    public List<InfrastructureModule> Modules { get; } = [];
    public List<InfrastructureDependency> Dependencies { get; } = [];
    public List<InfrastructureVariable> Variables { get; } = [];
    public List<string> Locals { get; } = [];
    public List<InfrastructureOutput> Outputs { get; } = [];
    public Dictionary<string, InfrastructureProvider> Providers { get; } = new(StringComparer.Ordinal);
    public List<InfrastructureBackend> Backends { get; } = [];
    public List<AccessAssignment> Access { get; } = [];
    public List<InfrastructureEndpointInjection> Injections { get; } = [];
    public Dictionary<string, (SourceEnvironmentLabel Label, HashSet<string> Files, string Basis)> Environments { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Unresolved { get; } = new(StringComparer.Ordinal);
    public List<SourceDomainDiagnostic> Diagnostics { get; } = [];
    public HashSet<string> Limitations { get; } = new(StringComparer.Ordinal) { SourceDomainText.SourceBoundary };
    public HashSet<InfrastructureFormat> Formats { get; } = [];
    public HashSet<string> Technologies { get; } = new(StringComparer.Ordinal);
    public List<PendingConfigurationValue> Pending { get; } = [];
    public bool Heuristic { get; set; }

    public void Environment(SourceEnvironmentLabel label, string file, string basis)
    {
        var key = $"{label.Kind}|{label.Raw.ToLowerInvariant()}";
        if (!Environments.TryGetValue(key, out var e)) Environments[key] = e = (label, new HashSet<string>(StringComparer.Ordinal), basis);
        e.Files.Add(SourceEvidenceRedaction.Safe(file));
    }

    // Analyzer v4: Terraform extras and infrastructure automation.
    public List<InfrastructureRequiredVersion> RequiredVersions { get; } = [];
    public List<InfrastructureLockedProvider> LockedProviders { get; } = [];
    public List<InfrastructureRemoteState> RemoteStates { get; } = [];
    public List<InfrastructureExcludedFile> ExcludedFiles { get; } = [];
    public List<InfrastructureScript> Scripts { get; } = [];
    public List<InfrastructureOtherScript> OtherScripts { get; } = [];
    public int OtherScriptCount { get; set; }
    public List<InfrastructureValidationCheck> Checks { get; } = [];
    public List<InfrastructureGeneratedArtifact> Artifacts { get; } = [];
    public List<InfrastructureScriptRelationship> ScriptRelationships { get; } = [];
    public List<InfrastructureScriptInvocation> Invocations { get; } = [];

    public const string ExternalModule = "External module not analyzed — module sources are never fetched.";

    public static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static string Id(string dir, string address) => $"{(dir.Length == 0 ? "." : dir)}/{address}";
    public static string Normalize(string path) => BirkNext.Api.Services.SourceArchitecture.ArchitectureInput.Normalize(path);

    public static SourceEnvironmentLabel? FolderEnvironment(string path)
    {
        foreach (var segment in Dir(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var env = SourceFileClassifier.Environment(segment);
            if (env.Kind is not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom)) return env;
        }
        return null;
    }

    public static string SafeSource(string source)
    {
        var s = Regex.Replace(source, @"\?.*$", "");
        s = Regex.Replace(s, @"//[^/@]+@", "//");
        return SourceEvidenceRedaction.Safe(s);
    }
}
