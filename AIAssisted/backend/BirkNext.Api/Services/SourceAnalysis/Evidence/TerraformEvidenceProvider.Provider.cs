using System.Text.RegularExpressions;
using BirkNext.SourceDomains;
using static BirkNext.Api.Services.SourceAnalysis.Evidence.InfrastructureEvidenceBuilder;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

internal sealed partial class TerraformEvidenceProvider
{
    public string Key => "terraform";
    public string Name => "Terraform evidence provider";
    /// <summary>v4: required_version, .terraform.lock.hcl, terraform_remote_state with source-layer matching, count/for_each as written.</summary>
    public int Version => 4;

    public IReadOnlyList<DomainCapability> Capabilities { get; } =
    [
        new(SourceEvidenceDomain.Infrastructure, "Terraform", DomainSupport.Supported, "HCL structure, resources, data sources, references, depends_on, local modules, variables, tfvars, outputs, providers, required_version, lock files, backend metadata and remote state (matched to source layers by name). Static only: literals, variables and locals are resolved; functions are not evaluated, count/for_each are not expanded, and no state, plan, registry or backend is read."),
        new(SourceEvidenceDomain.Infrastructure, "Terraform JSON (.tf.json)", DomainSupport.Unsupported, "Detected only; JSON-syntax Terraform is not analyzed."),
    ];

    public bool Claims(EvidenceFile file) => file.Technology is "Terraform" or "Terraform variables" or "Terraform lock file" or "Terraform JSON";

    public InfrastructureProviderRun Contribute(SourceEvidenceContext context, IReadOnlyList<EvidenceFile> files, InfrastructureEvidenceBuilder b, CancellationToken ct)
    {
        Terraform(b, files.Where(f => f.Technology is "Terraform" or "Terraform variables").ToList(), ct);
        var locks = files.Where(f => f.Technology == "Terraform lock file").ToList();
        foreach (var file in locks)
        {
            ct.ThrowIfCancellationRequested();
            var root = HclReader.Parse(file.Content, out var errors);
            if (errors.Count > 0) b.Diagnostics.Add(new("Parse error", "Terraform lock file could not be fully read.", SourceEvidenceRedaction.Safe(file.Path)));
            foreach (var provider in root.Blocks.Where(x => x.Type == "provider" && x.Labels.Count == 1))
                b.LockedProviders.Add(new(SourceEvidenceRedaction.SafePath(provider.Labels[0]), provider.Attribute("version") is { } v ? HclReader.Literal(v.Expression) is { } version ? SourceEvidenceRedaction.SafePath(version) : null : null,
                    provider.Attribute("constraints") is { } c ? HclReader.Literal(c.Expression) is { } constraints ? SourceEvidenceRedaction.SafePath(constraints) : null : null,
                    SourceEvidenceRedaction.Safe(file.Directory), SourceEvidenceRedaction.Safe(file.Path), provider.Line));
        }
        if (locks.Count > 0) b.Technologies.Add("Terraform lock file");
        var json = files.Where(f => f.Technology == "Terraform JSON").ToList();
        if (json.Count > 0)
            b.Diagnostics.Add(new("Unsupported format", $"{json.Count} Terraform JSON file(s) (.tf.json) detected; JSON-syntax Terraform is not analyzed.", SourceEvidenceRedaction.Safe(json[0].Path)));
        var tf = files.Count(f => f.Technology == "Terraform");
        var tfvars = files.Count(f => f.Technology == "Terraform variables");
        return new(Key, Name, Version, files.Count == 0 ? SourceDomainStatus.NotDetected : tf + tfvars == 0 ? SourceDomainStatus.Unsupported : SourceDomainStatus.Complete, files.Count,
            files.Count == 0 ? "No Terraform files in this source." : $"{tf} .tf, {tfvars} tfvars, {locks.Count} lock file(s){(json.Count > 0 ? $", {json.Count} .tf.json (not analyzed)" : "")}.");
    }

    private static string InstanceText(HclAttribute attribute)
    {
        var e = Regex.Replace(attribute.Expression, @"\s+", " ").Trim();
        return $"{attribute.Name} = {SourceEvidenceRedaction.SafePath(e.Length > 80 ? e[..80] + "…" : e)}";
    }

    /// <summary>count/for_each that can produce zero instances: a conditional (?:), or a count that is not a positive literal.</summary>
    private static bool IsConditional(HclAttribute attribute)
    {
        var e = attribute.Expression.Trim();
        if (e.Contains('?')) return true;
        return attribute.Name == "count" && !(int.TryParse(e, out var n) && n > 0);
    }

    private static readonly Regex RemoteOutput = new(@"data\.terraform_remote_state\.([A-Za-z_][A-Za-z0-9_\-]*)\.outputs\.([A-Za-z_][A-Za-z0-9_\-]*)", RegexOptions.Compiled);
    private static readonly Regex LocationSetting = new(@"(?m)^\s*""?(container_name|key|prefix|workspace|workspaces|path|bucket|name|organization)""?\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// data "terraform_remote_state": backend type, the state location NAMES the config gives (container/key/prefix/workspace/path), the outputs
    /// this layer reads, and the source layer whose directory name equals one of those names. The backend is never contacted; a name match
    /// is a strong source match at most, because the real state location is decided at init.
    /// </summary>
    private static void RemoteStates(InfrastructureEvidenceBuilder b, Dictionary<string, TfModuleScope> scopes, HashSet<string> moduleDirs)
    {
        var roots = scopes.Keys.Where(d => !moduleDirs.Contains(d)).ToList();
        foreach (var scope in scopes.Values.OrderBy(s => s.Directory, StringComparer.Ordinal))
        {
            var reads = scope.Blocks.SelectMany(x => x.Block.AllAttributes()).SelectMany(x => RemoteOutput.Matches(x.Attribute.Expression)).Select(m => (Name: m.Groups[1].Value, Output: m.Groups[2].Value)).ToList();
            foreach (var (file, block) in scope.Blocks.Where(x => x.Block.Type == "data" && x.Block.Labels.Count == 2 && x.Block.Labels[0] == "terraform_remote_state"))
            {
                var name = block.Labels[1];
                var backend = block.Attribute("backend") is { } be ? HclReader.Literal(be.Expression) : null;
                var config = block.Attribute("config")?.Expression ?? string.Join("\n", block.Nested("config").SelectMany(c => c.Attributes).Select(a => $"{a.Name} = {a.Expression}"));
                var locations = LocationSetting.Matches(config).Select(m => (Key: m.Groups[1].Value, Value: Regex.Replace(m.Groups[2].Value, @"\$\{[^}]*\}", "*"))).Where(l => l.Value.Trim('*').Length > 0).ToList();
                var candidates = new List<(string Dir, string Basis)>();
                foreach (var (key, value) in locations)
                {
                    var stem = Regex.Replace(value.Split('/').Last(), @"\.tfstate$", "", RegexOptions.IgnoreCase);
                    if (stem.Length == 0 || stem.Contains('*')) continue;
                    var matches = roots.Where(d => d != scope.Directory && d.Split('/').Last().Equals(stem, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) continue;
                    candidates.AddRange(matches.Select(m => (m, $"state {key} \"{SourceEvidenceRedaction.Safe(stem)}\" equals the layer directory name; the backend location is decided at init and is not verified")));
                    break;
                }
                var distinct = candidates.Select(c => c.Dir).Distinct(StringComparer.Ordinal).ToList();
                var layer = distinct.Count == 1 ? distinct[0] : null;
                var outputs = reads.Where(r => r.Name == name).Select(r => r.Output).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var declared = layer is null ? null : b.Outputs.Where(o => Dir(o.File) == SourceEvidenceRedaction.Safe(layer)).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
                b.RemoteStates.Add(new InfrastructureRemoteState
                {
                    Id = Id(scope.Directory, $"data.terraform_remote_state.{block.Labels[1]}"), Name = SourceEvidenceRedaction.Safe(name), Backend = backend is null ? null : SourceEvidenceRedaction.Safe(backend),
                    StateLocation = locations.Count == 0 ? null : string.Join(", ", locations.Where(l => l.Key is not "organization").Select(l => $"{l.Key} {SourceEvidenceRedaction.SafePath(l.Value)}")),
                    UsedBy = SourceEvidenceRedaction.Safe(scope.Directory), SourceLayer = layer is null ? null : SourceEvidenceRedaction.Safe(layer),
                    Resolution = distinct.Count switch { 1 => "Strong source match", > 1 => "Ambiguous", _ => "Unresolved" },
                    ResolutionBasis = distinct.Count switch
                    {
                        1 => candidates[0].Basis,
                        > 1 => $"Several layer directories are named like the state location ({string.Join(", ", distinct.Select(SourceEvidenceRedaction.Safe))}).",
                        _ => locations.Count == 0 ? "The config names no state location in source (configured at init)." : "No layer directory in this source is named like the state location.",
                    },
                    ReferencedOutputs = [.. outputs.Select(SourceEvidenceRedaction.Safe)],
                    OutputsNotDeclared = declared is null ? [] : [.. outputs.Where(o => !declared.Contains(SourceEvidenceRedaction.Safe(o))).Select(SourceEvidenceRedaction.Safe)],
                    File = SourceEvidenceRedaction.Safe(file.Path), Line = block.Line,
                });
            }
        }
    }
}
