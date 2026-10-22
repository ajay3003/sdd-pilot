using System.Text.Json.Serialization;

namespace BirkNext.SourceDomains;

// ── Infrastructure Source Evidence: provider family, Terraform extras and infrastructure automation scripts (Infrastructure analyzer v4) ──
//
// Declared in Terraform ≠ detected in a script ≠ validated by source-side script logic ≠ configured in BirkNext ≠ confirmed mapping ≠
// executed ≠ deployed ≠ observed at runtime ≠ healthy. Everything here is static source evidence: scripts are read, never run; Terraform is
// read, never initialised, planned or applied; no state, registry, backend or cloud is contacted.

/// <summary>One sibling provider of the Infrastructure evidence family and what it contributed to this snapshot.</summary>
public sealed record InfrastructureProviderRun(string Key, string Name, int Version, SourceDomainStatus Status, int Files, string Detail);

/// <summary>A <c>required_version</c> constraint as written in a <c>terraform</c> block.</summary>
public sealed record InfrastructureRequiredVersion(string Constraint, string Directory, string File, int Line);

/// <summary>A provider selection recorded in a <c>.terraform.lock.hcl</c> file (version and constraints as written; hashes are not kept).</summary>
public sealed record InfrastructureLockedProvider(string Address, string? Version, string? Constraints, string Directory, string File, int Line);

/// <summary>A file the infrastructure evidence intentionally did not read (Terraform state, binary plans), with the reason.</summary>
public sealed record InfrastructureExcludedFile(string Path, string Reason);

/// <summary>
/// A <c>data "terraform_remote_state"</c> read of another layer's outputs. The backend is never contacted: <see cref="SourceLayer"/> is the
/// source directory whose name matches the state location named in the config (container, key, prefix, workspace or path), with
/// <see cref="Resolution"/> saying how certain that is.
/// </summary>
public sealed record InfrastructureRemoteState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Backend { get; init; }
    /// <summary>The state location as written (container/key/prefix/workspace/path names only — never access keys or SAS tokens).</summary>
    public string? StateLocation { get; init; }
    /// <summary>The directory that reads the remote state.</summary>
    public string UsedBy { get; init; } = "";
    public string? SourceLayer { get; init; }
    /// <summary>"Strong source match", "Ambiguous" or "Unresolved" — a name match, never a backend lookup.</summary>
    public string Resolution { get; init; } = "Unresolved";
    public string ResolutionBasis { get; init; } = "";
    public List<string> ReferencedOutputs { get; init; } = [];
    /// <summary>Outputs read here that the matched source layer does not declare in this source.</summary>
    public List<string> OutputsNotDeclared { get; init; } = [];
    public string File { get; init; } = "";
    public int Line { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureScriptLanguage { Python, Shell, PowerShell }

/// <summary>What a script is for, decided from its content (commands, file access, rules, outputs) — never from its file name alone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureScriptPurpose { Validation, PolicyCheck, InventoryGeneration, DocumentationGeneration, VersionCheck, NamingConventionCheck, DeploymentHelper, MigrationHelper, Reporting, Unknown }

/// <summary>Strong: the script's own code works on infrastructure source (Terraform files, resource types, terraform/az commands).
/// Supporting: only how it is invoked (a pipeline or hook that targets infrastructure) says so. None: not infrastructure automation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureScriptRelevance { Strong, Supporting, None }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureStaticAnalysisState { Complete, Partial, ParseError, Skipped }

/// <summary>How a check behaves when it finds a problem, read from its exit paths. Says nothing about whether it ran or passed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureCheckEnforcement { FailsOnViolation, Advisory, ReportOnly, NotDetermined }

/// <summary>Whether a configured invocation can stop its pipeline/commit: Gate (a failure fails the step), FailureTolerated (|| echo, || true,
/// continueOnError), NotDetermined. Configured as a gate is not "passed".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureInvocationBlocking { Gate, FailureTolerated, NotDetermined }

/// <summary>A command the script would invoke if run — a static reference only; nothing is executed.</summary>
public sealed record InfrastructureScriptCommand(string Command, string? Subcommand, int Line, bool ExternalTool);

/// <summary>An input the script reads: "Command-line argument", "Path", "File pattern" or "Standard input".</summary>
public sealed record InfrastructureScriptInput(string Kind, string Value, int Line);

/// <summary>A file the script writes, as statically knowable ("File write", "Redirect", "Tool output").</summary>
public sealed record InfrastructureScriptOutput(string Kind, string Path, int Line);

/// <summary>An environment variable the script reads. The value is never resolved from the host; sensitive names are flagged.</summary>
public sealed record InfrastructureScriptEnvironmentVariable(string Name, bool Sensitive, int Line);

/// <summary>A neutral, source-side description of what the script could do if run ("May access network", "May delete files") — not a
/// vulnerability and not a severity.</summary>
public sealed record InfrastructureScriptCapability(string Capability, string Detail, int Line);

public sealed record InfrastructureScript
{
    /// <summary>The relative source path (also the stable key).</summary>
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public InfrastructureScriptLanguage Language { get; init; }
    public InfrastructureScriptRelevance Relevance { get; init; }
    public List<string> RelevanceBasis { get; init; } = [];
    public InfrastructureScriptPurpose PrimaryPurpose { get; init; } = InfrastructureScriptPurpose.Unknown;
    public List<InfrastructureScriptPurpose> Purposes { get; init; } = [];
    public List<string> PurposeBasis { get; init; } = [];
    /// <summary>"__main__ guard", "main()", "top-level statements", "param() block"…</summary>
    public string? EntryPoint { get; init; }
    public List<string> Functions { get; init; } = [];
    public List<string> Imports { get; init; } = [];
    /// <summary>Local files this script includes (Python local imports, shell source/., PowerShell dot-sourcing) that exist in the snapshot.</summary>
    public List<string> LocalIncludes { get; init; } = [];
    public List<InfrastructureScriptCommand> Commands { get; init; } = [];
    public List<InfrastructureScriptInput> Inputs { get; init; } = [];
    public List<InfrastructureScriptOutput> Outputs { get; init; } = [];
    public List<InfrastructureScriptEnvironmentVariable> EnvironmentVariables { get; init; } = [];
    public List<string> CliArguments { get; init; } = [];
    /// <summary>Terraform concepts the code references: resource types (azurerm_role_assignment), blocks (required_providers, module),
    /// files (.terraform.lock.hcl, *.tf, *.tfvars) and tools (terraform-docs).</summary>
    public List<string> TerraformConcepts { get; init; } = [];
    /// <summary>The failure conditions the source states (its own error messages), placeholders shown as "…".</summary>
    public List<string> ValidationRules { get; init; } = [];
    /// <summary>"Extracted", "Partial" or "Not applicable".</summary>
    public string RulesState { get; init; } = "Not applicable";
    public string? RulesReason { get; init; }
    public InfrastructureCheckEnforcement Enforcement { get; init; } = InfrastructureCheckEnforcement.NotDetermined;
    public string? EnforcementBasis { get; init; }
    public List<InfrastructureScriptCapability> Capabilities { get; init; } = [];
    public bool DynamicExecution { get; init; }
    /// <summary>Programs embedded in the script and analyzed with it, e.g. "Python (heredoc, line 25)".</summary>
    public List<string> EmbeddedPrograms { get; init; } = [];
    public InfrastructureStaticAnalysisState AnalysisState { get; init; } = InfrastructureStaticAnalysisState.Complete;
    public string? AnalysisNote { get; init; }
    public int Lines { get; init; }
    public string ExecutionState { get; init; } = InfrastructureAutomationText.NotExecuted;
}

/// <summary>A script that was read but is not infrastructure automation (application code, tooling setup), with the reason.</summary>
public sealed record InfrastructureOtherScript(string Path, InfrastructureScriptLanguage Language, string Reason);

/// <summary>
/// A source-side check: logic in the repository intended to validate infrastructure source. "Check implemented" is all Source Analysis proves;
/// referenced by CI comes from pipeline evidence; executed and passed need run evidence and are never claimed here.
/// Several scripts enforcing the same policy are one check with several implementations.
/// </summary>
public sealed record InfrastructureValidationCheck
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public InfrastructureScriptPurpose Type { get; init; }
    /// <summary>What it inspects, e.g. "azurerm_role_assignment declarations".</summary>
    public string Scope { get; init; } = "";
    public List<string> Subjects { get; init; } = [];
    public List<string> Implementations { get; init; } = [];
    public List<string> Rules { get; init; } = [];
    public string RulesState { get; init; } = "Partial";
    public InfrastructureCheckEnforcement Enforcement { get; init; } = InfrastructureCheckEnforcement.NotDetermined;
    /// <summary>Declarations in this snapshot the check's subjects match (null when the subject is not a countable declaration).</summary>
    public int? MatchingDeclarations { get; init; }
    public string ImplementationState { get; init; } = InfrastructureAutomationText.CheckImplemented;
    public string ExecutionState { get; init; } = InfrastructureAutomationText.NotExecuted;
}

/// <summary>An artifact a script's source writes. Its definition is source evidence; whether a copy exists is only what this source package
/// contains (a checked-in copy), never "generated by this analysis".</summary>
public sealed record InfrastructureGeneratedArtifact
{
    public string Id { get; init; } = "";
    /// <summary>"Inventory", "Documentation", "Report" or "Data file".</summary>
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string PathTemplate { get; init; } = "";
    public string GeneratedBy { get; init; } = "";
    public List<string> Inputs { get; init; } = [];
    public int Line { get; init; }
    public bool PresentInSource { get; init; }
    public string PresenceNote { get; init; } = "";
}

/// <summary>A static relationship between a script and infrastructure source: "Validates role assignments" → azurerm_role_assignment (42
/// declarations). Validated by script logic means the logic exists — not that it ran or passed.</summary>
public sealed record InfrastructureScriptRelationship(string ScriptId, string Relation, string Target, string TargetKind, int? Matches, string Basis);

/// <summary>Where a script is configured to run: a pipeline step or a pre-commit hook. Configured ≠ executed ≠ passed.</summary>
public sealed record InfrastructureScriptInvocation
{
    public string ScriptId { get; init; } = "";
    /// <summary>"Pipeline" or "Pre-commit hook".</summary>
    public string Source { get; init; } = "";
    /// <summary>Pipeline definition id or hook id.</summary>
    public string Reference { get; init; } = "";
    public string? Pipeline { get; init; }
    public string? Stage { get; init; }
    public string? Job { get; init; }
    public string? Step { get; init; }
    public string? Interpreter { get; init; }
    public InfrastructureInvocationBlocking Blocking { get; init; } = InfrastructureInvocationBlocking.NotDetermined;
    public string BlockingBasis { get; init; } = "";
    /// <summary>When it is configured to run ("on commit of files matching \.tf$", "push to main", "manual").</summary>
    public string? Trigger { get; init; }
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string ExecutionState { get; init; } = InfrastructureAutomationText.ConfiguredNotAssessed;
}

public static class InfrastructureAutomationText
{
    public const int AutomationAnalyzerVersion = 4;
    public const string NotExecuted = "Not executed by analysis";
    public const string CheckImplemented = "Check implemented in source";
    public const string ConfiguredNotAssessed = "Configured; execution not assessed";
    public const string StateExcluded = "Sensitive Terraform state intentionally excluded.";
    public const string PlanExcluded = "Binary Terraform plan file not parsed.";
    public const string NotAvailableInVersion = "Infrastructure script evidence was not available in this analysis version.";
    public const string NoInfrastructureSource = "No infrastructure source evidence found in this source.";

    public static string Label(InfrastructureScriptPurpose purpose) => purpose switch
    {
        InfrastructureScriptPurpose.PolicyCheck => "Policy check",
        InfrastructureScriptPurpose.InventoryGeneration => "Inventory generation",
        InfrastructureScriptPurpose.DocumentationGeneration => "Documentation generation",
        InfrastructureScriptPurpose.VersionCheck => "Version check",
        InfrastructureScriptPurpose.NamingConventionCheck => "Naming/convention check",
        InfrastructureScriptPurpose.DeploymentHelper => "Deployment helper",
        InfrastructureScriptPurpose.MigrationHelper => "Migration/helper",
        _ => purpose.ToString(),
    };

    public static string Label(InfrastructureCheckEnforcement enforcement) => enforcement switch
    {
        InfrastructureCheckEnforcement.FailsOnViolation => "Fails on violation",
        InfrastructureCheckEnforcement.Advisory => "Advisory (warnings, exits 0)",
        InfrastructureCheckEnforcement.ReportOnly => "Report only (exits 0)",
        _ => "Not determined",
    };

    public static string Label(InfrastructureInvocationBlocking blocking) => blocking switch
    {
        InfrastructureInvocationBlocking.Gate => "Configured as gate",
        InfrastructureInvocationBlocking.FailureTolerated => "Failure tolerated",
        _ => "Gate not determined",
    };

    public static string Label(InfrastructureStaticAnalysisState state) => state switch
    {
        InfrastructureStaticAnalysisState.Complete => "Static analysis complete",
        InfrastructureStaticAnalysisState.Partial => "Static analysis partial",
        InfrastructureStaticAnalysisState.ParseError => "Parse error",
        _ => "Skipped",
    };

    public static bool IsCheck(InfrastructureScriptPurpose purpose) => purpose is InfrastructureScriptPurpose.Validation or InfrastructureScriptPurpose.PolicyCheck
        or InfrastructureScriptPurpose.VersionCheck or InfrastructureScriptPurpose.NamingConventionCheck;
}

/// <summary>The layers of infrastructure evidence for one resource type or check, kept apart: declared in Terraform, validation logic in
/// source, configured in a pipeline/hook — and the layers Source Analysis can never prove (executed, passed, observed at runtime).</summary>
public sealed record InfrastructureEvidenceLayers(
    int Declared, IReadOnlyList<InfrastructureValidationCheck> Checks, IReadOnlyList<InfrastructureScriptInvocation> PipelineReferences,
    IReadOnlyList<InfrastructureScriptInvocation> HookReferences)
{
    public bool DeclaredInTerraform => Declared > 0;
    public bool ValidationLogicAvailable => Checks.Count > 0;
    public bool ReferencedByPipeline => PipelineReferences.Count > 0;
    public bool ConfiguredAsPipelineGate => PipelineReferences.Any(i => i.Blocking == InfrastructureInvocationBlocking.Gate);
    /// <summary>Always "Not assessed": static source evidence cannot show that a check ran or passed.</summary>
    public string Execution => "Not assessed";
    public string Runtime => "Not assessed";
}

/// <summary>
/// The shared read side of the Infrastructure Source Evidence: pure functions over ONE stored snapshot's <see cref="InfrastructureEvidence"/>
/// (no file is read and nothing is re-parsed). Every consumer — Source Analysis, Pipeline Review, Integrations, IQR, Topology, Environment
/// Analysis, Impact, Traceability and documentation — asks the same questions the same way.
/// </summary>
public static class InfrastructureEvidenceQueries
{
    public static IEnumerable<InfrastructureResource> Resources(InfrastructureEvidence e, params InfrastructureCategory[] categories) =>
        e.Resources.Where(r => r.Kind == "resource" && (categories.Length == 0 || categories.Contains(r.Category)));

    public static IEnumerable<InfrastructureScript> Scripts(InfrastructureEvidence e, params InfrastructureScriptPurpose[] purposes) =>
        e.Scripts.Where(s => purposes.Length == 0 || s.Purposes.Any(purposes.Contains));

    public static IEnumerable<InfrastructureValidationCheck> Checks(InfrastructureEvidence e) => e.Checks;

    /// <summary>A resource by id, Terraform address (type.name), logical name or declared name (case-insensitive) — null when not unique.</summary>
    public static InfrastructureResource? FindResourceByIdentifier(InfrastructureEvidence e, string identifier)
    {
        var id = identifier.Trim();
        var exact = e.Resources.FirstOrDefault(r => r.Id == id);
        if (exact is not null) return exact;
        var matches = e.Resources.Where(r => r.Id.EndsWith("/" + id, StringComparison.Ordinal) || $"{r.ResourceType}.{r.LogicalName}" == id
            || string.Equals(r.DeclaredName, id, StringComparison.OrdinalIgnoreCase) || r.EnvironmentNames.Any(n => string.Equals(n.Name, id, StringComparison.OrdinalIgnoreCase))).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static IEnumerable<InfrastructureValidationCheck> FindChecksForResourceType(InfrastructureEvidence e, string resourceType) =>
        e.Checks.Where(c => c.Subjects.Contains(resourceType, StringComparer.Ordinal));

    public static IEnumerable<InfrastructureValidationCheck> FindChecksForResource(InfrastructureEvidence e, InfrastructureResource resource) =>
        FindChecksForResourceType(e, resource.ResourceType);

    public static IEnumerable<InfrastructureScriptInvocation> Invocations(InfrastructureEvidence e, string scriptId) => e.ScriptInvocations.Where(i => i.ScriptId == scriptId);

    /// <summary>The infrastructure scripts a pipeline (definition id or file) is configured to run.</summary>
    public static IEnumerable<(InfrastructureScriptInvocation Invocation, InfrastructureScript Script)> FindScriptsUsedByPipeline(InfrastructureEvidence e, string pipelineIdOrFile) =>
        e.ScriptInvocations.Where(i => i.Source == "Pipeline" && (i.Reference == pipelineIdOrFile || i.File == pipelineIdOrFile))
            .Select(i => (i, e.Scripts.FirstOrDefault(s => s.Id == i.ScriptId)))
            .Where(x => x.Item2 is not null).Select(x => (x.i, x.Item2!));

    /// <summary>The script a pipeline step runs and the checks that script implements — what a generic "run script" step actually checks.</summary>
    public static (InfrastructureScript Script, IReadOnlyList<InfrastructureValidationCheck> Checks)? ForPipelineScript(InfrastructureEvidence e, string scriptPath)
    {
        var path = scriptPath.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        var script = e.Scripts.FirstOrDefault(s => s.Id == path) ?? e.Scripts.Where(s => s.Id.EndsWith("/" + path, StringComparison.Ordinal)).Take(2).ToList() switch { [var only] => only, _ => null };
        return script is null ? null : (script, ChecksImplementedBy(e, script.Id).ToList());
    }

    public static IEnumerable<InfrastructureValidationCheck> ChecksImplementedBy(InfrastructureEvidence e, string scriptId) => e.Checks.Where(c => c.Implementations.Contains(scriptId));

    public static IEnumerable<InfrastructureGeneratedArtifact> FindGeneratedArtifacts(InfrastructureEvidence e, string? scriptId = null) =>
        e.GeneratedArtifacts.Where(a => scriptId is null || a.GeneratedBy == scriptId);

    /// <summary>Declared / source validation / pipeline configuration for a resource type; execution and runtime stay "Not assessed".</summary>
    public static InfrastructureEvidenceLayers Layers(InfrastructureEvidence e, string resourceType)
    {
        var checks = FindChecksForResourceType(e, resourceType).ToList();
        var invocations = checks.SelectMany(c => c.Implementations).SelectMany(s => Invocations(e, s)).Distinct().ToList();
        return new(e.Resources.Count(r => r.Kind == "resource" && r.ResourceType == resourceType), checks,
            invocations.Where(i => i.Source == "Pipeline").ToList(), invocations.Where(i => i.Source != "Pipeline").ToList());
    }

    /// <summary>Pipeline-configured infrastructure quality gates: pipeline → script (gate, failure not tolerated) → the checks it implements.
    /// Configured as a gate; never "passed".</summary>
    public static IEnumerable<(InfrastructureScriptInvocation Invocation, InfrastructureValidationCheck Check)> PipelineGates(InfrastructureEvidence e) =>
        e.ScriptInvocations.Where(i => i.Source == "Pipeline" && i.Blocking == InfrastructureInvocationBlocking.Gate)
            .SelectMany(i => ChecksImplementedBy(e, i.ScriptId).Select(c => (i, c)));
}

/// <summary>Automation implementation evidence for one requirement (traceability): the source check that implements it, the declarations it inspects,
/// where it is configured to run — and execution/pass, which source evidence never establishes.</summary>
public sealed record AutomationImplementationEvidence(InfrastructureValidationCheck Check, int Declarations, IReadOnlyList<InfrastructureScriptInvocation> PipelineReferences,
    IReadOnlyList<InfrastructureScriptInvocation> HookReferences)
{
    public string Implementation => "Automation implementation evidence available (check implemented in source)";
    public string Execution => "Not assessed";
}

public static class InfrastructureTraceability
{
    private static readonly Dictionary<string, string[]> SubjectTerms = new(StringComparer.Ordinal)
    {
        ["azurerm_role_assignment"] = ["rbac", "role assignment", "role-assignment", "access assignment"],
        [".terraform.lock.hcl"] = ["provider", "lock file"],
        ["required_providers"] = ["provider"],
        ["Azure Verified Modules (avm-*)"] = ["module", "avm"],
        ["module blocks"] = ["module"],
        ["names = { … } blocks"] = ["naming", "abbreviation", "caf"],
        ["*.tf"] = ["terraform"],
    };

    /// <summary>Checks a requirement's text names by subject AND by what the check enforces (naming, description, version, pinning, formatting):
    /// "RBAC naming must be validated" → RBAC naming validation. Never a match on one generic word; never a pass claim.</summary>
    public static IReadOnlyList<AutomationImplementationEvidence> For(InfrastructureEvidence e, string requirement)
    {
        var text = requirement.ToLowerInvariant().Replace('_', ' ');
        var result = new List<AutomationImplementationEvidence>();
        foreach (var check in e.Checks)
        {
            var subjectNamed = check.Subjects.Any(s => (SubjectTerms.TryGetValue(s, out var terms) ? terms : [s.Replace('_', ' ')]).Any(text.Contains));
            var aspect = System.Text.RegularExpressions.Regex.Match(check.Name.ToLowerInvariant(), @"(naming|description|version pinning|pinning|version|abbreviation|formatting)").Value;
            var aspectNamed = aspect.Length > 0 && (text.Contains(aspect) || aspect == "version pinning" && (text.Contains("pinn") || text.Contains("version"))
                || aspect == "naming" && text.Contains("name") || aspect == "formatting" && text.Contains("format"));
            if (!subjectNamed || !aspectNamed) continue;
            var invocations = check.Implementations.SelectMany(id => InfrastructureEvidenceQueries.Invocations(e, id)).ToList();
            result.Add(new(check, check.MatchingDeclarations ?? 0, invocations.Where(i => i.Source == "Pipeline").ToList(), invocations.Where(i => i.Source != "Pipeline").ToList()));
        }
        return result;
    }
}
