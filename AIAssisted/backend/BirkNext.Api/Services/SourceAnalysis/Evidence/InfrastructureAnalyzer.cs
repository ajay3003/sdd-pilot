using System.Text.RegularExpressions;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Source Analysis → Infrastructure Source Evidence: a family of sibling providers that write ONE normalized Infrastructure evidence per
/// snapshot — Terraform (structural HCL), declarative manifests (Bicep, ARM, Kubernetes, Helm; by pattern) and infrastructure automation
/// scripts (Python, shell, PowerShell; static). Nothing is executed: no terraform init/plan/apply, no script run, no module, provider or
/// registry download, no state, no backend, no cloud call. Every resource is "declared in source"; every check is "implemented in source";
/// existence, effective permission, reachability, delivery, execution and results are runtime questions other reviews answer.
/// </summary>
internal sealed class InfrastructureAnalyzer : ISourceEvidenceDomainAnalyzer
{
    /// <summary>v2: per-environment declared names (tfvars). v3: module deployment names and endpoint injections. v4: the provider family —
    /// required_version, lock files, remote state, count/for_each as written, excluded state files and infrastructure automation scripts (static;
    /// reads the CI/CD domain's script references). Snapshots analysed earlier keep their evidence; nothing is reinterpreted.</summary>
    public const int Version = InfrastructureAutomationText.AutomationAnalyzerVersion;

    internal static IReadOnlyList<IInfrastructureSourceEvidenceProvider> Providers { get; } =
        [new TerraformEvidenceProvider(), new DeclarativeManifestEvidenceProvider(), new InfrastructureScriptEvidenceProvider()];

    public DomainAnalyzerInfo Info { get; } = SourceEvidenceAnalyzer.Info(SourceEvidenceDomain.Infrastructure, "Infrastructure Source Evidence analyzer", Version, 1,
        ["Terraform", "Terraform variables", "Terraform lock file", "Bicep", "ARM template", "Kubernetes manifest", "Helm chart", "Python scripts", "Shell scripts", "PowerShell scripts"],
        // Scripts are classified with the pipelines that run them: the CI/CD domain is read (never re-parsed) for script references.
        [SourceEvidenceDomain.CiCd],
        ["Resources", "Data sources", "Modules", "Static dependencies", "Variables", "Outputs", "Providers", "Backends", "Access assignments", "Environments", "Endpoint injections",
         "Required versions", "Lock files", "Remote state", "Infrastructure scripts", "Validation checks", "Generated artifact definitions", "Script relationships", "Script invocations"]);

    public void Failed(SourceEvidenceContext context, string reason) => context.Infrastructure = context.Envelope(new InfrastructureEvidence
    { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason, Limitations = [SourceDomainText.SourceBoundary] }, SourceEvidenceDomain.Infrastructure, Version);

    private static readonly Regex StateFile = new(@"(^|/)[^/]*\.tfstate(\.backup)?$|(^|/)terraform\.tfstate\.d/", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlanFile = new(@"(^|/)[^/]*\.(tfplan|plan)$|(^|/)tfplan$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Analyze(SourceEvidenceContext context, CancellationToken ct)
    {
        foreach (var provider in Providers) context.Capabilities.AddRange(provider.Capabilities);
        var b = new InfrastructureEvidenceBuilder();
        foreach (var path in context.AllPaths)
        {
            if (StateFile.IsMatch(path)) b.ExcludedFiles.Add(new(SourceEvidenceRedaction.Safe(path), InfrastructureAutomationText.StateExcluded));
            else if (PlanFile.IsMatch(path) && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) b.ExcludedFiles.Add(new(SourceEvidenceRedaction.Safe(path), InfrastructureAutomationText.PlanExcluded));
        }
        var runs = new List<InfrastructureProviderRun>();
        foreach (var provider in Providers)
        {
            ct.ThrowIfCancellationRequested();
            var files = context.Files.Where(provider.Claims).ToList();
            runs.Add(provider.Contribute(context, files, b, ct));
        }
        context.PendingConfiguration.AddRange(b.Pending);

        var iac = context.Role(SourceFileRole.InfrastructureAsCode).Count();
        if (iac == 0 && b.Scripts.Count == 0)
        {
            context.Infrastructure = context.Envelope(new InfrastructureEvidence
            {
                Status = SourceDomainStatus.NotDetected,
                StatusReason = "No supported Infrastructure as Code files or infrastructure automation scripts detected in the selected source. Infrastructure may live in another repository.",
                Limitations = [SourceDomainText.SourceBoundary], ProviderRuns = runs, ExcludedFiles = b.ExcludedFiles, OtherScripts = b.OtherScripts, OtherScriptCount = b.OtherScriptCount,
            }, SourceEvidenceDomain.Infrastructure, Version);
            return;
        }
        var analyzedAny = b.Formats.Count > 0 || b.Scripts.Count > 0;
        var status = !analyzedAny ? SourceDomainStatus.Unsupported
            : b.Heuristic || b.Unresolved.Count > 0 || b.Modules.Any(m => !m.Analyzed) || b.Diagnostics.Any(d => d.Kind is "Parse error" or "Unsupported format")
              || b.Scripts.Any(s => s.AnalysisState != InfrastructureStaticAnalysisState.Complete) ? SourceDomainStatus.Partial
            : SourceDomainStatus.Complete;
        if (b.Scripts.Count > 0) b.Limitations.Add("Infrastructure scripts are analyzed statically and never executed: implemented checks are not executed or passed checks.");
        context.Infrastructure = context.Envelope(new InfrastructureEvidence
        {
            Status = status,
            StatusReason = status switch
            {
                SourceDomainStatus.Unsupported => "Infrastructure as Code was detected, but only in unsupported formats.",
                SourceDomainStatus.Partial => "Some declarations or scripts could only be read partially (heuristic formats, external modules, unresolved references, dynamic script logic or parse errors).",
                _ => null,
            },
            Formats = [.. b.Formats.Order()], Technologies = [.. b.Technologies.Order(StringComparer.Ordinal)],
            Providers = [.. b.Providers.Values.OrderBy(p => p.Name, StringComparer.Ordinal)], Modules = b.Modules, Resources = b.Resources,
            Variables = b.Variables, Locals = [.. b.Locals.Distinct().Order(StringComparer.Ordinal)], Outputs = b.Outputs,
            Dependencies = [.. b.Dependencies.DistinctBy(d => (d.FromId, d.ToId, d.Kind))], Backends = b.Backends, AccessAssignments = b.Access,
            EndpointInjections = [.. b.Injections.DistinctBy(i => (i.FromId, i.Key, i.ToId))],
            Environments = [.. b.Environments.Values.OrderBy(e => e.Label.Kind).ThenBy(e => e.Label.Raw, StringComparer.Ordinal).Select(e => new InfrastructureEnvironment(e.Label, [.. e.Files.Order(StringComparer.Ordinal)], e.Basis))],
            UnresolvedReferences = [.. b.Unresolved.Order(StringComparer.Ordinal)], Diagnostics = b.Diagnostics, Limitations = [.. b.Limitations],
            ProviderRuns = runs, RequiredVersions = b.RequiredVersions, LockedProviders = b.LockedProviders, RemoteStates = b.RemoteStates, ExcludedFiles = b.ExcludedFiles,
            Scripts = b.Scripts, OtherScripts = b.OtherScripts, OtherScriptCount = b.OtherScriptCount, Checks = b.Checks, GeneratedArtifacts = b.Artifacts,
            ScriptRelationships = b.ScriptRelationships, ScriptInvocations = b.Invocations,
        }, SourceEvidenceDomain.Infrastructure, Version);
    }
}
