using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>
/// Presentation of infrastructure automation evidence (Source Analysis → Infrastructure → Automation &amp; checks, Pipeline Review, IQR):
/// one row per source-side check or other automation, with where it is configured to run and what is known about execution — which, from
/// source alone, is always "Not executed by analysis". Wording never says "Validated" or "Passed" for logic that merely exists.
/// </summary>
public static class InfrastructureAutomationPresentation
{
    public sealed record Row(string Key, string Name, string Type, IReadOnlyList<InfrastructureScript> Scripts, string Scope, string PipelineUsage, string ExecutionEvidence,
        InfrastructureValidationCheck? Check);

    /// <summary>Checks first (aggregated over their implementations), then every other infrastructure script (inventories, documentation,
    /// deployment helpers, reports) that implements no check.</summary>
    public static IReadOnlyList<Row> Rows(InfrastructureEvidence e)
    {
        var rows = new List<Row>();
        foreach (var check in e.Checks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var scripts = check.Implementations.Select(id => e.Scripts.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Select(s => s!).ToList();
            rows.Add(new(check.Id, check.Name, InfrastructureAutomationText.Label(check.Type), scripts, check.Scope, Usage(e, check.Implementations), Execution(e, check.Implementations), check));
        }
        var implemented = e.Checks.SelectMany(c => c.Implementations).ToHashSet(StringComparer.Ordinal);
        foreach (var script in e.Scripts.Where(s => !implemented.Contains(s.Id)).OrderBy(s => s.PrimaryPurpose).ThenBy(s => s.Path, StringComparer.Ordinal))
            rows.Add(new(script.Id, Title(script), InfrastructureAutomationText.Label(script.PrimaryPurpose), [script],
                script.TerraformConcepts.Count == 0 ? "—" : string.Join(", ", script.TerraformConcepts.Take(3)) + (script.TerraformConcepts.Count > 3 ? ", …" : ""),
                Usage(e, [script.Id]), Execution(e, [script.Id]), null));
        return rows;
    }

    /// <summary>A readable name for automation that is not a check: "Identity inventory generation", "Terraform documentation generation".</summary>
    public static string Title(InfrastructureScript script)
    {
        var output = script.Outputs.FirstOrDefault()?.Path;
        return script.PrimaryPurpose switch
        {
            InfrastructureScriptPurpose.InventoryGeneration when output is not null => $"{Humanize(System.IO.Path.GetFileNameWithoutExtension(output))} generation",
            InfrastructureScriptPurpose.DocumentationGeneration when script.Commands.Any(c => c.Command == "terraform-docs") => "Terraform documentation generation",
            InfrastructureScriptPurpose.DocumentationGeneration => "Documentation generation",
            InfrastructureScriptPurpose.DeploymentHelper => "Deployment helper",
            InfrastructureScriptPurpose.MigrationHelper => "Migration helper",
            InfrastructureScriptPurpose.Reporting => "Infrastructure report",
            _ => System.IO.Path.GetFileNameWithoutExtension(script.Path),
        };
    }

    private static string Humanize(string name)
    {
        var words = name.Replace('-', ' ').Replace('_', ' ').Trim();
        var acronyms = new[] { "rbac", "caf", "avm", "iac" };
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => acronyms.Contains(w.ToLowerInvariant()) ? w.ToUpperInvariant() : w.ToLowerInvariant()).ToList();
        if (parts.Count > 0 && parts[0].Length > 0 && char.IsLower(parts[0][0])) parts[0] = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return string.Join(' ', parts);
    }

    /// <summary>"Pipeline m2lb-version-check (failure tolerated) · Pre-commit hook (commit gate)" — configuration, never execution.</summary>
    public static string Usage(InfrastructureEvidence e, IEnumerable<string> scriptIds)
    {
        var invocations = scriptIds.SelectMany(id => InfrastructureEvidenceQueries.Invocations(e, id)).ToList();
        if (invocations.Count == 0) return "Not referenced by a pipeline or hook";
        return string.Join(" · ", invocations.GroupBy(i => (i.Source, i.Pipeline ?? i.Reference, i.Blocking)).Select(g => g.Key.Source == "Pipeline"
            ? $"Pipeline {g.Key.Item2} ({(g.Key.Blocking == InfrastructureInvocationBlocking.FailureTolerated ? "failure tolerated" : g.Key.Blocking == InfrastructureInvocationBlocking.Gate ? "configured as gate" : "gate not determined")})"
            : $"Pre-commit hook {g.Key.Item2} (commit gate on developer machines)"));
    }

    /// <summary>What the source proves about execution: never more than "referenced"; run evidence is a separate layer.</summary>
    public static string Execution(InfrastructureEvidence e, IEnumerable<string> scriptIds) =>
        scriptIds.Any(id => InfrastructureEvidenceQueries.Invocations(e, id).Any(i => i.Source == "Pipeline"))
            ? "Referenced by pipeline · not executed by analysis"
            : InfrastructureAutomationText.NotExecuted;

    public static string Relevance(InfrastructureScriptRelevance relevance) => relevance switch
    {
        InfrastructureScriptRelevance.Strong => "Infrastructure script detected",
        InfrastructureScriptRelevance.Supporting => "Infrastructure context from its pipeline/hook",
        _ => "Not infrastructure automation",
    };

    public static string Tone(InfrastructureStaticAnalysisState state) => state == InfrastructureStaticAnalysisState.Complete ? "muted" : "partial";

    /// <summary>A summary sentence for consumers: "6 source checks (2 referenced by a pipeline) · execution not assessed".</summary>
    public static string Summary(InfrastructureEvidence e)
    {
        if (!e.AutomationAnalyzed) return InfrastructureAutomationText.NotAvailableInVersion;
        if (e.Scripts.Count == 0) return "No infrastructure automation scripts in this source.";
        var referenced = e.Checks.Count(c => c.Implementations.Any(id => InfrastructureEvidenceQueries.Invocations(e, id).Any(i => i.Source == "Pipeline")));
        return $"{e.Scripts.Count} infrastructure script(s) · {e.Checks.Count} source check(s){(referenced > 0 ? $" ({referenced} referenced by a pipeline)" : "")} · execution not assessed";
    }
}
