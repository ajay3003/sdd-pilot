using BirkNext.PipelineReview;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.PipelineReview;

/// <summary>
/// Pipeline Review ↔ Infrastructure Source Evidence: a step that runs a repository infrastructure script is described by what the script checks
/// (its source checks, purpose and behaviour on violation) instead of "Run Python script". The pipeline definition proves the check is configured
/// to run; it never proves the script ran or passed.
/// </summary>
internal static class PipelineInfrastructureChecks
{
    public static ReviewedTest WithInfrastructure(this ReviewedTest test, InfrastructureEvidence? infrastructure, PipelineStep step)
    {
        if (infrastructure is null || step.Script is not { } reference || InfrastructureEvidenceQueries.ForPipelineScript(infrastructure, reference.Path) is not { } found) return test;
        var (script, checks) = found;
        return test with
        {
            Script = script.Path,
            ScriptPurpose = $"{InfrastructureAutomationText.Label(script.PrimaryPurpose)} · {InfrastructureAutomationText.Label(script.Enforcement)}",
            InfrastructureChecks = [.. checks.Select(c => c.Name)],
            ScriptGate = reference.FailureTolerated || step.ContinueOnError ? "Failure tolerated" : "Configured as gate",
        };
    }
}
