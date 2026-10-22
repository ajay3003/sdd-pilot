using System.Text.RegularExpressions;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Infrastructure evidence provider: infrastructure automation scripts (Python, shell, PowerShell), read statically — never executed, sourced,
/// imported or evaluated. A script is claimed as infrastructure automation only when its own content works on infrastructure source
/// (Terraform files and blocks, resource types, terraform/az commands) or its invocation context does (a pipeline that deploys
/// infrastructure, a hook on .tf changes); the file name and folder are never enough. For each script: purpose (from commands, file access,
/// rules and outputs), enforcement (from its exit paths), validation rules (its own failure messages), commands, inputs, outputs, environment
/// variable NAMES, capabilities, relationships to the snapshot's Terraform declarations and where it is configured to run. Script constants
/// never become configuration values.
/// </summary>
internal sealed class InfrastructureScriptEvidenceProvider : IInfrastructureSourceEvidenceProvider
{
    public const int MaxScriptBytes = 512 * 1024;
    private const int MaxOtherScripts = 50;

    public string Key => "infrastructure-scripts";
    public string Name => "Infrastructure script evidence provider";
    public int Version => 1;

    public IReadOnlyList<DomainCapability> Capabilities { get; } =
    [
        new(SourceEvidenceDomain.Infrastructure, "Python infrastructure scripts", DomainSupport.Supported, "Static tokenizer analysis: imports, functions, entry point, subprocess/os.system commands, file reads, writes and globs, environment variable names, CLI arguments, failure messages and exit statuses. Never executed or imported; eval/exec make it partial."),
        new(SourceEvidenceDomain.Infrastructure, "Shell infrastructure scripts", DomainSupport.Supported, "Static analysis of sh/bash: commands and subcommands, redirections, stderr messages, exit statuses, set -e, positional arguments, source/. includes and embedded Python heredocs. Never executed or sourced; eval and computed command names make it partial."),
        new(SourceEvidenceDomain.Infrastructure, "PowerShell infrastructure scripts", DomainSupport.Partial, "Line-based: cmdlets and native tools, file reads/writes, $env: names, param() arguments, throw/Write-Error messages, exit statuses and dot-sourcing. Never executed; Invoke-Expression makes it partial."),
        new(SourceEvidenceDomain.Infrastructure, "Script execution", DomainSupport.Unsupported, "Not performed. Scripts are read, never run: executed and passed are never claimed from source."),
    ];

    public bool Claims(EvidenceFile file) => file.Role == SourceFileRole.Script;

    private static readonly Regex ResourceType = new(@"\b((?:azurerm|azuread|azapi|aws|google|kubernetes|helm|random|tls|time|null|github|azuredevops|cloudflare|datadog)_[a-z0-9]+(?:_[a-z0-9]+)*)\b", RegexOptions.Compiled);
    private static readonly Regex TfFile = new(@"(\*\.tf\b|\.tf[""'$]|\\\.tf\$|\.tf\)|^\.tf$)", RegexOptions.Compiled);
    private static readonly Regex Location = new(@"^(?:…|[\w./\-]+)(?::(?:…|\d+))*:\s+", RegexOptions.Compiled);
    private static readonly Regex Subject = new(@"^[A-Za-z_][\w]*\.…:\s*", RegexOptions.Compiled);

    private sealed record Invocation(InfrastructureScriptInvocation Evidence, bool InfrastructureContext, string ContextLabel);

    public InfrastructureProviderRun Contribute(SourceEvidenceContext context, IReadOnlyList<EvidenceFile> files, InfrastructureEvidenceBuilder b, CancellationToken ct)
    {
        if (files.Count == 0)
            return new(Key, Name, Version, SourceDomainStatus.NotDetected, 0, "No Python, shell or PowerShell scripts in this source.");
        var paths = (context.AllPaths.Count > 0 ? context.AllPaths : context.Files.Select(f => f.Path)).ToHashSet(StringComparer.Ordinal);
        var scriptPaths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var invocations = PipelineInvocations(context, scriptPaths).Concat(HookInvocations(context, scriptPaths)).ToList();
        var snapshot = new SnapshotFacts(b, context);
        var other = 0;
        var candidates = new List<(InfrastructureScript Script, ScriptFacts Facts, Classification C)>();
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var language = Language(file);
            var invoked = invocations.Where(i => i.Evidence.ScriptId == file.Path).ToList();
            if (file.Content.Length > MaxScriptBytes)
            {
                if (invoked.Count > 0)
                    candidates.Add((new InfrastructureScript
                    {
                        Id = Safe(file.Path), Path = Safe(file.Path), Language = language, Relevance = InfrastructureScriptRelevance.Supporting,
                        RelevanceBasis = [.. invoked.Select(i => i.ContextLabel).Distinct()], AnalysisState = InfrastructureStaticAnalysisState.Skipped,
                        AnalysisNote = "Larger than the 512 KB static analysis limit; not analyzed.",
                    }, new ScriptFacts { ScriptPath = file.Path }, Classification.Empty));
                else Other(b, ref other, file.Path, language, "Larger than the 512 KB static analysis limit; not analyzed.");
                continue;
            }
            var facts = new ScriptFacts { ScriptPath = file.Path };
            try
            {
                switch (language)
                {
                    case InfrastructureScriptLanguage.Python: PythonStaticReader.Read(file.Content, facts); break;
                    case InfrastructureScriptLanguage.Shell: ShellStaticReader.Read(file.Content, facts); break;
                    default: PowerShellStaticReader.Read(file.Content, facts); break;
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                // One unreadable script never stops the analysis; the exception text is not stored (it could quote source).
                facts.ParseError = true;
                facts.Partial($"The static reader could not process this script ({ex.GetType().Name}); other scripts are unaffected.");
            }
            var c = Classify(facts, invoked, snapshot);
            if (c.Relevance == InfrastructureScriptRelevance.None) { Other(b, ref other, file.Path, language, c.NotInfrastructureReason); continue; }
            candidates.Add((Script(file, language, facts, c, paths), facts, c));
        }
        foreach (var (script, facts, c) in candidates)
        {
            b.Scripts.Add(script);
            if (script.AnalysisState == InfrastructureStaticAnalysisState.Skipped) continue;
            b.ScriptRelationships.AddRange(Relationships(script, c, snapshot));
            b.Artifacts.AddRange(Artifacts(script, facts, c, paths));
        }
        b.Checks.AddRange(Checks(candidates.Where(x => x.Script.AnalysisState != InfrastructureStaticAnalysisState.Skipped && InfrastructureAutomationText.IsCheck(x.Script.PrimaryPurpose)).ToList(), snapshot));
        var infrastructure = b.Scripts.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        b.Invocations.AddRange(invocations.Where(i => infrastructure.Contains(Safe(i.Evidence.ScriptId))).Select(i => i.Evidence with { ScriptId = Safe(i.Evidence.ScriptId) }));
        foreach (var g in b.Scripts.GroupBy(s => s.Language)) b.Technologies.Add($"{g.Key} infrastructure scripts");
        var partial = b.Scripts.Count(s => s.AnalysisState != InfrastructureStaticAnalysisState.Complete);
        return new(Key, Name, Version, b.Scripts.Count == 0 ? SourceDomainStatus.NotDetected : partial > 0 ? SourceDomainStatus.Partial : SourceDomainStatus.Complete, files.Count,
            $"{files.Count} script(s) read statically; {b.Scripts.Count} infrastructure automation, {other} other. None executed.");
    }

    private static string Safe(string path) => SourceEvidenceRedaction.Safe(path);

    private static void Other(InfrastructureEvidenceBuilder b, ref int count, string path, InfrastructureScriptLanguage language, string reason)
    {
        count++;
        b.OtherScriptCount = count;
        if (b.OtherScripts.Count < MaxOtherScripts) b.OtherScripts.Add(new(Safe(path), language, reason));
    }

    private static InfrastructureScriptLanguage Language(EvidenceFile file) => file.Technology switch
    {
        "Python" => InfrastructureScriptLanguage.Python,
        "PowerShell" => InfrastructureScriptLanguage.PowerShell,
        _ => InfrastructureScriptLanguage.Shell,
    };

    // ── snapshot facts the classification correlates with (counts only; nothing is re-read) ──────────────────────────────

    private sealed class SnapshotFacts(InfrastructureEvidenceBuilder b, SourceEvidenceContext context)
    {
        public Dictionary<string, int> ResourceTypes { get; } = b.Resources.Where(r => r.Format == InfrastructureFormat.Terraform && r.Kind == "resource")
            .GroupBy(r => r.ResourceType, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        public int Modules { get; } = b.Modules.Count(m => m.File.EndsWith(".tf", StringComparison.Ordinal));
        public int AvmModules { get; } = b.Modules.Count(m => m.Source.Contains("avm-", StringComparison.OrdinalIgnoreCase));
        public int LockFiles { get; } = b.LockedProviders.Select(p => p.File).Distinct().Count();
        public int RemoteStates { get; } = b.RemoteStates.Count;
        public int TerraformDirectories { get; } = context.Files.Where(f => f.Technology == "Terraform").Select(f => f.Directory).Distinct().Count();
        public int TerraformFiles { get; } = context.Files.Count(f => f.Technology == "Terraform");
        public int RequiredProviders { get; } = b.Providers.Values.Count(p => p.Version is not null);
    }

    // ── classification ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Classification
    {
        public static Classification Empty { get; } = new();
        public InfrastructureScriptRelevance Relevance { get; init; } = InfrastructureScriptRelevance.None;
        public List<string> RelevanceBasis { get; init; } = [];
        public string NotInfrastructureReason { get; init; } = "";
        public List<string> ResourceTypes { get; init; } = [];
        public List<string> Concepts { get; init; } = [];
        public List<InfrastructureScriptPurpose> Purposes { get; init; } = [];
        public List<string> PurposeBasis { get; init; } = [];
        public InfrastructureCheckEnforcement Enforcement { get; init; }
        public string? EnforcementBasis { get; init; }
        public List<string> Rules { get; init; } = [];
        public string Aspect { get; init; } = "";
        public string SubjectLabel { get; init; } = "Infrastructure";
        public bool Generator => Purposes.Contains(InfrastructureScriptPurpose.InventoryGeneration) || Purposes.Contains(InfrastructureScriptPurpose.DocumentationGeneration);
    }

    private static Classification Classify(ScriptFacts f, List<Invocation> invoked, SnapshotFacts snapshot)
    {
        var literals = f.Literals.Select(l => l.Text).ToList();
        // A resource type counts only when this snapshot declares it or the provider catalog knows it ("aws_access_key_id" is a variable name).
        var types = literals.SelectMany(l => ResourceType.Matches(l).Select(m => m.Groups[1].Value))
            .Where(t => snapshot.ResourceTypes.ContainsKey(t) || InfrastructureCatalog.Terraform(t).Category != InfrastructureCategory.Other)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(20).ToList();
        var concepts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var l in literals)
        {
            if (l.Contains("required_providers", StringComparison.Ordinal)) concepts.Add("required_providers");
            if (l.Contains("terraform_remote_state", StringComparison.Ordinal)) concepts.Add("terraform_remote_state");
            if (l.Contains(".terraform.lock.hcl", StringComparison.Ordinal)) concepts.Add(".terraform.lock.hcl");
            if (Regex.IsMatch(l, @"\.tfvars\b")) concepts.Add("*.tfvars");
            if (TfFile.IsMatch(l)) concepts.Add("*.tf");
            if (Regex.IsMatch(l, @"\^module[ \\[]|resource\s*\\?s?\+?\s*\\?""?module|\bmodule\s+""")) concepts.Add("module blocks");
            if (l.Contains("avm-", StringComparison.OrdinalIgnoreCase)) concepts.Add("Azure Verified Modules (avm-*)");
            if (Regex.IsMatch(l, @"\bnames\b[^\n]{0,40}=[^\n]{0,40}\\?\{")) concepts.Add("names = { … } blocks");
            if (l.Contains("registry.terraform.io", StringComparison.OrdinalIgnoreCase)) concepts.Add("Terraform Registry");
            if (Regex.IsMatch(l, @"\bprovider\s+\\?""")) concepts.Add("provider blocks");
        }
        foreach (var input in f.Inputs)
        {
            if (Regex.IsMatch(input.Value, @"\.tf$|\*\.tf\b")) concepts.Add("*.tf");
            if (input.Value.EndsWith(".tfvars", StringComparison.Ordinal)) concepts.Add("*.tfvars");
            if (input.Value.Contains(".terraform.lock.hcl", StringComparison.Ordinal)) concepts.Add(".terraform.lock.hcl");
        }
        var terraform = f.Commands.Where(c => c.Command == "terraform" && c.Subcommand is { } s && !s.StartsWith("version") && !s.StartsWith("help")).Select(c => c.Subcommand!).Distinct().ToList();
        var iacTools = f.Commands.Where(c => c.Command is "terraform-docs" or "tflint" or "tfsec" or "checkov" && c.Subcommand is not null).Select(c => c.Command).Distinct().ToList();
        var az = f.Commands.Where(c => c.Command == "az" && c.Subcommand is { } s && !Regex.IsMatch(s, @"^(version|login|logout|account|extension|upgrade|config|bicep)\b")).Select(c => c.Subcommand!).Distinct().ToList();
        if (iacTools.Contains("terraform-docs")) concepts.Add("terraform-docs");
        if (terraform.Count > 0 || iacTools.Count > 0) concepts.Add("terraform CLI");

        var strong = new List<string>();
        if (types.Count > 0) strong.Add($"References Terraform resource types ({string.Join(", ", types.Take(3))}{(types.Count > 3 ? ", …" : "")})");
        if (concepts.Overlaps(["*.tf", "*.tfvars", ".terraform.lock.hcl"])) strong.Add($"Reads Terraform source ({string.Join(", ", concepts.Where(x => x is "*.tf" or "*.tfvars" or ".terraform.lock.hcl"))})");
        string[] blocks = ["required_providers", "terraform_remote_state", "module blocks", "provider blocks", "names = { … } blocks"];
        if (concepts.Overlaps(blocks)) strong.Add($"Inspects Terraform blocks ({string.Join(", ", concepts.Where(blocks.Contains))})");
        foreach (var t in terraform.Take(3)) strong.Add($"Runs terraform {t}");
        foreach (var tool in iacTools) strong.Add($"Runs {tool}");
        if (az.Count > 0) strong.Add($"Runs Azure CLI ({string.Join(", ", az.Take(2).Select(s => $"az {s}"))})");
        var supporting = invoked.Where(i => i.InfrastructureContext).Select(i => i.ContextLabel).Distinct().ToList();
        var relevance = strong.Count > 0 ? InfrastructureScriptRelevance.Strong : supporting.Count > 0 ? InfrastructureScriptRelevance.Supporting : InfrastructureScriptRelevance.None;
        if (relevance == InfrastructureScriptRelevance.None)
            return new Classification
            {
                NotInfrastructureReason = f.Capabilities.Any(c => c.Capability == "Installs tools")
                    ? "Developer tooling setup: installs or checks tools; it does not read or change infrastructure source."
                    : f.ParseError ? "Could not be read statically, and no pipeline or hook runs it in an infrastructure context."
                    : "No infrastructure source, Terraform/Azure command or infrastructure pipeline/hook context in its content.",
            };

        // Purpose, from content: outputs, tools, rules and exit paths (the header comment only breaks ties).
        var messages = f.Messages.Select(m => m.Text).ToList();
        var words = string.Join(' ', messages.Concat(f.Identifiers)).ToLowerInvariant();
        var header = string.Join(' ', f.HeaderComments).ToLowerInvariant();
        var outputs = f.Outputs.Where(o => o.Path.Trim('*', '/').Length > 0).ToList();
        var inventory = outputs.Any(o => o.Path.Contains("inventory", StringComparison.OrdinalIgnoreCase))
            || outputs.Count > 0 && literals.Any(l => Regex.IsMatch(l, @"(?i)\binventory\b"));
        var markdown = outputs.Any(o => o.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var failing = f.ExitCodes.Any(c => c != 0) || f.ConditionalFailure;
        var alwaysZero = f.ExitCodes.Count > 0 && f.ExitCodes.All(c => c == 0) && !f.ConditionalFailure;
        var rules = f.Messages.Where(m => !m.Warning).ToList();
        var warnings = f.Messages.Where(m => m.Warning).ToList();
        var versionSignal = concepts.Overlaps([".terraform.lock.hcl", "required_providers", "Terraform Registry"]) || f.Commands.Any(c => c.Command == "git" && c.Subcommand == "ls-remote")
            || Regex.IsMatch(words, @"\bversions?\b|vkey|semver");
        var aspect = Regex.IsMatch(string.Join(' ', messages).ToLowerInvariant(), @"description") ? "description"
            : Regex.IsMatch(string.Join(' ', messages).ToLowerInvariant(), @"\bpinn|\bconstraint") ? "version pinning"
            : Regex.IsMatch(words, @"abbreviation|\bcaf\b") ? "abbreviation"
            : Regex.IsMatch(words, @"\bnam(e|es|ing)\b|convention|prefix") && rules.Concat(warnings).Any(m => Regex.IsMatch(m.Text, @"(?i)nam(e|ing)|convention|prefix|abbreviation")) ? "naming"
            : versionSignal ? "version"
            : terraform.Any(t => t.StartsWith("fmt")) ? "formatting"
            : Regex.IsMatch(header, @"\bpinn") && rules.Count > 0 ? "version pinning"
            : "";
        var purposes = new List<InfrastructureScriptPurpose>();
        var basis = new List<string>();
        void Add(InfrastructureScriptPurpose p, string why) { if (!purposes.Contains(p)) { purposes.Add(p); basis.Add(why); } }
        if (iacTools.Contains("terraform-docs")) Add(InfrastructureScriptPurpose.DocumentationGeneration, "Invokes terraform-docs to write module documentation");
        if (inventory) Add(InfrastructureScriptPurpose.InventoryGeneration, $"Writes {string.Join(", ", outputs.Select(o => o.Path).Distinct().Take(2))}; its content names an inventory");
        else if (markdown && !iacTools.Contains("terraform-docs")) Add(InfrastructureScriptPurpose.DocumentationGeneration, $"Writes Markdown ({string.Join(", ", outputs.Where(o => o.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Select(o => o.Path).Distinct().Take(2))})");
        var generator = purposes.Count > 0;
        // Running a validation tool (terraform fmt -check / validate, tflint, tfsec, checkov) is the purpose unless the script states a policy.
        var toolValidation = terraform.Any(t => t.StartsWith("fmt") || t.StartsWith("validate")) || iacTools.Any(t => t is "tflint" or "tfsec" or "checkov");
        if (!generator && toolValidation && aspect is "" or "formatting")
            Add(InfrastructureScriptPurpose.Validation, $"Runs {string.Join(", ", terraform.Where(t => t.StartsWith("fmt") || t.StartsWith("validate")).Select(t => $"terraform {t}").Concat(iacTools.Where(t => t != "terraform-docs")))}");
        if (!generator)
        {
            if (aspect is "naming" or "abbreviation" && (rules.Count > 0 || warnings.Count > 0))
                Add(InfrastructureScriptPurpose.NamingConventionCheck, $"Reports naming/convention problems ({rules.Count + warnings.Count} message(s))");
            if (rules.Count > 0 && failing && aspect is not ("naming" or "abbreviation") && !(toolValidation && aspect is "" or "formatting"))
                Add(InfrastructureScriptPurpose.PolicyCheck, $"Reports {(aspect.Length > 0 ? aspect : "policy")} violations and has a non-zero exit path");
            if (versionSignal) Add(InfrastructureScriptPurpose.VersionCheck, "Reads provider/module versions (lock files, version constraints or release tags)");
            if (terraform.Any(t => t.StartsWith("fmt") || t.StartsWith("validate")) || iacTools.Any(t => t is "tflint" or "tfsec" or "checkov"))
                Add(InfrastructureScriptPurpose.Validation, $"Runs {string.Join(", ", terraform.Where(t => t.StartsWith("fmt") || t.StartsWith("validate")).Select(t => $"terraform {t}").Concat(iacTools.Where(t => t != "terraform-docs")))}");
            if (failing && rules.Count > 0 && purposes.Count == 0) Add(InfrastructureScriptPurpose.Validation, "Reports problems and has a non-zero exit path");
        }
        if (terraform.Any(t => Regex.IsMatch(t, @"^(apply|plan|init|destroy)")) || az.Any(s => s.StartsWith("deployment"))) Add(InfrastructureScriptPurpose.DeploymentHelper, "Runs terraform apply/plan/init or az deployment");
        if (terraform.Any(t => Regex.IsMatch(t, @"^(state|import)")) || Regex.IsMatch(words, @"migrat")) Add(InfrastructureScriptPurpose.MigrationHelper, "Runs terraform state/import or migration logic");
        if (purposes.Count == 0 && (f.PrintsOutput || outputs.Count > 0)) Add(InfrastructureScriptPurpose.Reporting, "Prints or writes results without a failing exit path");
        if (purposes.Count == 0) Add(InfrastructureScriptPurpose.Unknown, "Infrastructure relevance found, but its purpose could not be determined statically");

        var (enforcement, enforcementBasis) = failing
            ? (InfrastructureCheckEnforcement.FailsOnViolation, f.ExitCodes.Any(c => c != 0) ? "Has an explicit non-zero exit status" : "Exit status is computed from its findings")
            : alwaysZero || (f.ExitCodes.Count == 0 && (f.PrintsOutput || warnings.Count > 0))
                ? warnings.Count > 0 ? (InfrastructureCheckEnforcement.Advisory, "Reports warnings and exits 0") : (InfrastructureCheckEnforcement.ReportOnly, "Prints a report; no failing exit path")
                : f.ErrExit ? (InfrastructureCheckEnforcement.NotDetermined, "Stops on command errors (set -e) only; no explicit violation status") : (InfrastructureCheckEnforcement.NotDetermined, "No exit status found in source");

        var ruleTexts = (enforcement == InfrastructureCheckEnforcement.Advisory ? warnings : rules).Select(m => Rule(m.Text)).Where(r => r is not null).Select(r => r!)
            .Distinct(StringComparer.Ordinal).Take(6).ToList();
        return new Classification
        {
            Relevance = relevance, RelevanceBasis = [.. strong, .. supporting], ResourceTypes = types, Concepts = [.. concepts], Purposes = purposes, PurposeBasis = basis,
            Enforcement = generator && !failing ? InfrastructureCheckEnforcement.NotDetermined : enforcement,
            EnforcementBasis = generator && !failing ? "Generator: writes files; no violation status" : enforcementBasis,
            Rules = ruleTexts, Aspect = aspect, SubjectLabel = SubjectLabel(types, concepts, words, aspect),
        };
    }

    /// <summary>A failure message as a rule: the location prefix ("…:…:", "file.tf:12:") and subject ("azurerm_role_assignment.…:") removed;
    /// placeholders stay "…"; null when nothing descriptive remains.</summary>
    private static string? Rule(string message)
    {
        var r = Regex.Replace(message, @"\s+", " ").Trim();
        if (Regex.IsMatch(r, @"(?i)^(…\s*:\s*)?(cannot read|could not read|error reading|usage:)")) return null;
        for (var i = 0; i < 3; i++) { var before = r; r = Location.Replace(r, ""); r = Subject.Replace(r, ""); if (r == before) break; }
        r = r.Trim(' ', ':', '-');
        if (r.Count(char.IsLetter) < 8) return null;
        if (r.Length > 160) r = r[..160] + "…";
        return SourceEvidenceRedaction.SafeText(r);
    }

    private static string SubjectLabel(List<string> types, SortedSet<string> concepts, string words, string aspect)
    {
        if (aspect == "abbreviation" && Regex.IsMatch(words, @"\bcaf\b|caf_abbreviations")) return "CAF";
        if (types.Contains("azurerm_role_assignment")) return "RBAC";
        if (types.Any(t => t.StartsWith("azuread_", StringComparison.Ordinal) || t == "azurerm_user_assigned_identity")) return "Identity";
        if (concepts.Contains("Azure Verified Modules (avm-*)")) return "AVM module";
        if (concepts.Overlaps([".terraform.lock.hcl", "required_providers", "Terraform Registry", "provider blocks"])) return "Provider";
        if (concepts.Contains("module blocks")) return "Module";
        if (types.Count > 0) return InfrastructureCatalog.Terraform(types[0]).Detail;
        if (concepts.Contains("*.tf") || concepts.Contains("terraform CLI")) return "Terraform";
        return "Infrastructure";
    }

    private static InfrastructureScript Script(EvidenceFile file, InfrastructureScriptLanguage language, ScriptFacts f, Classification c, HashSet<string> paths)
    {
        var dir = f.ScriptDirectory;
        var includes = f.Includes.Select(i => Include(i.Raw, dir, language, paths)).Where(p => p is not null).Select(p => Safe(p!)).Distinct(StringComparer.Ordinal).Take(20).ToList();
        var state = f.ParseError ? InfrastructureStaticAnalysisState.ParseError : f.PartialReason is not null || f.Dynamic ? InfrastructureStaticAnalysisState.Partial : InfrastructureStaticAnalysisState.Complete;
        var rulesState = c.Rules.Count > 0 ? state == InfrastructureStaticAnalysisState.Complete ? "Extracted" : "Partial"
            : InfrastructureAutomationText.IsCheck(c.Purposes.FirstOrDefault()) && c.Enforcement != InfrastructureCheckEnforcement.ReportOnly ? "Partial" : "Not applicable";
        return new InfrastructureScript
        {
            Id = Safe(file.Path), Path = Safe(file.Path), Language = language, Relevance = c.Relevance, RelevanceBasis = c.RelevanceBasis.Select(SourceEvidenceRedaction.SafeText).ToList(),
            PrimaryPurpose = c.Purposes.FirstOrDefault(InfrastructureScriptPurpose.Unknown), Purposes = c.Purposes, PurposeBasis = c.PurposeBasis.Select(SourceEvidenceRedaction.SafeText).ToList(),
            EntryPoint = f.EntryPoint, Functions = f.Functions.Select(SourceEvidenceRedaction.Safe).Distinct(StringComparer.Ordinal).Take(40).ToList(),
            Imports = f.Imports.Where(i => !i.StartsWith('.')).Select(SourceEvidenceRedaction.Safe).Distinct(StringComparer.Ordinal).Take(40).ToList(), LocalIncludes = includes,
            Commands = f.Commands.Select(x => new InfrastructureScriptCommand(SourceEvidenceRedaction.Safe(x.Command), x.Subcommand is null ? null : SourceEvidenceRedaction.SafePath(x.Subcommand), x.Line,
                !ShellStaticReader.IsCoreUtility(x.Command) && x.Command != "local script")).Take(60).ToList(),
            Inputs = f.Inputs.Where(i => i.Value.Trim('*', '/', '.').Length > 0).Select(i => new InfrastructureScriptInput(i.Kind, SourceEvidenceRedaction.SafePath(i.Value), i.Line)).DistinctBy(i => (i.Kind, i.Value)).Take(30).ToList(),
            Outputs = f.Outputs.Where(o => o.Path.Trim('*', '/').Length > 0).Select(o => new InfrastructureScriptOutput(o.Kind, SourceEvidenceRedaction.SafePath(o.Path), o.Line)).DistinctBy(o => o.Path).Take(30).ToList(),
            EnvironmentVariables = f.EnvironmentVariables.DistinctBy(e => e.Name).Take(40)
                .Select(e => new InfrastructureScriptEnvironmentVariable(SourceEvidenceRedaction.Safe(e.Name), SourceEvidenceRedaction.SensitiveKey(e.Name), e.Line)).ToList(),
            CliArguments = f.CliArguments.Select(SourceEvidenceRedaction.SafePath).Distinct(StringComparer.Ordinal).Take(20).ToList(),
            TerraformConcepts = [.. c.ResourceTypes, .. c.Concepts],
            ValidationRules = c.Rules, RulesState = rulesState,
            RulesReason = rulesState == "Partial" ? c.Rules.Count > 0 ? f.PartialReason is { } p ? SourceEvidenceRedaction.SafeText(p) : "Some logic could not be statically resolved." : "Complex dynamic logic could not be statically resolved into rules." : null,
            Enforcement = c.Enforcement, EnforcementBasis = c.EnforcementBasis,
            Capabilities = f.Capabilities.Select(x => new InfrastructureScriptCapability(x.Capability, SourceEvidenceRedaction.SafeText(Regex.Replace(x.Detail, @"[^@\s/]+@", "")), x.Line)).Take(20).ToList(),
            DynamicExecution = f.Dynamic, EmbeddedPrograms = [.. f.Embedded], AnalysisState = state,
            AnalysisNote = f.PartialReason is { } reason ? SourceEvidenceRedaction.SafeText(reason) : null, Lines = f.Lines,
        };
    }

    private static string? Include(string raw, string dir, InfrastructureScriptLanguage language, HashSet<string> paths)
    {
        if (language == InfrastructureScriptLanguage.Python && !raw.Contains('/'))
        {
            var module = raw.TrimStart('.').Replace('.', '/');
            if (module.Length == 0) return null;
            foreach (var candidate in new[] { dir.Length == 0 ? $"{module}.py" : $"{dir}/{module}.py", $"{module}.py", dir.Length == 0 ? $"{module}/__init__.py" : $"{dir}/{module}/__init__.py" })
                if (paths.Contains(candidate)) return candidate;
            return null;
        }
        return ScriptPathResolver.Resolve(Regex.Replace(raw, @"^\$\(dirname\s+""?\$0""?\)/|^\$\{?SCRIPT_DIR\}?/|^\$\{?BASH_SOURCE%/\*\}?/", ""), dir, paths);
    }

    // ── relationships, artifacts, checks ────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<InfrastructureScriptRelationship> Relationships(InfrastructureScript script, Classification c, SnapshotFacts s)
    {
        var check = InfrastructureAutomationText.IsCheck(script.PrimaryPurpose);
        foreach (var type in c.ResourceTypes)
        {
            var detail = InfrastructureCatalog.Terraform(type).Detail;
            var noun = (detail.Length > 0 ? detail : type).ToLowerInvariant();
            var relation = c.Generator ? $"Lists {noun} declarations" : check ? $"Validates {noun} declarations" : $"References {noun} declarations";
            yield return new(script.Id, relation, type, "Resource type", s.ResourceTypes.GetValueOrDefault(type), "Resource type named in the script's own patterns");
        }
        if (c.Concepts.Contains(".terraform.lock.hcl")) yield return new(script.Id, "Reads provider lock files", ".terraform.lock.hcl", "Lock file", s.LockFiles, "Lock-file pattern in the script");
        if (c.Concepts.Contains("required_providers")) yield return new(script.Id, check ? "Validates provider version constraints" : "Reads provider version constraints", "required_providers", "Terraform block", s.RequiredProviders, "Block name in the script");
        if (c.Concepts.Contains("Azure Verified Modules (avm-*)") || c.Concepts.Contains("module blocks"))
        {
            var avm = c.Concepts.Contains("Azure Verified Modules (avm-*)");
            yield return new(script.Id, c.Aspect == "version pinning" ? "Checks module version pinning" : check ? "Checks module versions" : "Reads module blocks",
                avm ? "module blocks (Azure/avm-*)" : "module blocks", "Terraform block", avm ? s.AvmModules : s.Modules, "Module pattern in the script");
        }
        if (c.Concepts.Contains("names = { … } blocks")) yield return new(script.Id, "Checks resource name prefixes", "names = { … } blocks", "Terraform block", null, "Pattern in the script");
        if (c.Concepts.Contains("terraform_remote_state")) yield return new(script.Id, "Reads remote-state declarations", "terraform_remote_state", "Terraform block", s.RemoteStates, "Block name in the script");
        if (c.Concepts.Contains("terraform-docs")) yield return new(script.Id, "Generates Terraform documentation", "Terraform directories", "Directory", s.TerraformDirectories, "Invokes terraform-docs");
        else if (c.Concepts.Contains("*.tf") && c.ResourceTypes.Count == 0 && !c.Concepts.Contains("Azure Verified Modules (avm-*)") && !c.Concepts.Contains("module blocks"))
            yield return new(script.Id, "Reads Terraform files", "*.tf", "Files", s.TerraformFiles, "Terraform file pattern in the script");
        foreach (var command in script.Commands.Where(x => x.Command == "terraform" && x.Subcommand is not null).Select(x => x.Subcommand!).Distinct())
            yield return new(script.Id, $"Runs terraform {command}", "terraform CLI", "Command", null, "Static command reference (not executed)");
    }

    private static IEnumerable<InfrastructureGeneratedArtifact> Artifacts(InfrastructureScript script, ScriptFacts f, Classification c, HashSet<string> paths)
    {
        foreach (var output in script.Outputs.DistinctBy(o => o.Path))
        {
            var docs = output.Kind == "Tool output" && c.Concepts.Contains("terraform-docs");
            var template = docs && !output.Path.Contains('/') ? $"*/{output.Path}" : output.Path;
            var name = System.IO.Path.GetFileName(template);
            if (name.Trim('*').Length == 0) continue;
            var kind = c.Purposes.Contains(InfrastructureScriptPurpose.InventoryGeneration) && (name.Contains("inventory", StringComparison.OrdinalIgnoreCase) || script.Outputs.Count == 1) ? "Inventory"
                : name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "Documentation"
                : Regex.IsMatch(name, @"\.(json|csv|ya?ml|txt|tsv)$", RegexOptions.IgnoreCase) ? "Data file" : "Report";
            var pattern = "^" + Regex.Escape(template).Replace(@"\*", "[^/]+(/[^/]+)*") + "$";
            var present = template.Contains('*') ? paths.Count(p => Regex.IsMatch(p, pattern)) : paths.Contains(template) ? 1 : 0;
            yield return new InfrastructureGeneratedArtifact
            {
                Id = $"{script.Id}#{template}", Kind = kind, Name = docs ? $"{name} (per Terraform directory)" : name, PathTemplate = template, GeneratedBy = script.Id,
                Inputs = [.. c.ResourceTypes.Concat(c.Concepts.Where(x => x is not "terraform CLI")).Take(6)], Line = output.Line, PresentInSource = present > 0,
                PresenceNote = present == 0 ? "Not present in this source package." : template.Contains('*')
                    ? $"{present} matching file(s) in this source package (checked-in copies; not produced by this analysis)."
                    : "A checked-in copy exists in this source package; it was not produced by this analysis.",
            };
        }
    }

    private static IEnumerable<InfrastructureValidationCheck> Checks(List<(InfrastructureScript Script, ScriptFacts Facts, Classification C)> items, SnapshotFacts s)
    {
        foreach (var group in items.GroupBy(x => CheckName(x.Script.PrimaryPurpose, x.C), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var subjects = group.SelectMany(x => x.C.ResourceTypes).Concat(group.SelectMany(x => x.C.Concepts.Where(c => c is ".terraform.lock.hcl" or "required_providers" or "module blocks" or "Azure Verified Modules (avm-*)" or "names = { … } blocks" or "*.tf")))
                .Distinct(StringComparer.Ordinal).ToList();
            var resourceTypes = group.SelectMany(x => x.C.ResourceTypes).Distinct(StringComparer.Ordinal).ToList();
            int? matches = resourceTypes.Count > 0 ? resourceTypes.Sum(t => s.ResourceTypes.GetValueOrDefault(t))
                : subjects.Contains("Azure Verified Modules (avm-*)") ? s.AvmModules : subjects.Contains("module blocks") ? s.Modules
                : subjects.Contains(".terraform.lock.hcl") ? s.LockFiles : null;
            var enforcement = group.Select(x => x.C.Enforcement).Distinct().ToList() is { Count: 1 } single ? single[0]
                : group.Any(x => x.C.Enforcement == InfrastructureCheckEnforcement.FailsOnViolation) ? InfrastructureCheckEnforcement.FailsOnViolation : InfrastructureCheckEnforcement.NotDetermined;
            var rules = group.SelectMany(x => x.Script.ValidationRules).Distinct(StringComparer.Ordinal).Take(8).ToList();
            yield return new InfrastructureValidationCheck
            {
                Id = "check:" + Regex.Replace(group.Key.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-'), Name = group.Key, Type = first.Script.PrimaryPurpose,
                Scope = Scope(subjects, first.C), Subjects = subjects, Implementations = [.. group.Select(x => x.Script.Id).Order(StringComparer.Ordinal)], Rules = rules,
                RulesState = group.All(x => x.Script.RulesState == "Extracted") ? "Extracted" : group.All(x => x.Script.RulesState == "Not applicable") ? "Not applicable" : "Partial",
                Enforcement = enforcement, MatchingDeclarations = matches,
            };
        }
    }

    private static string CheckName(InfrastructureScriptPurpose purpose, Classification c)
    {
        var suffix = purpose == InfrastructureScriptPurpose.VersionCheck ? c.Enforcement == InfrastructureCheckEnforcement.ReportOnly ? "report" : "check" : "validation";
        var aspect = c.Aspect.Length > 0 ? c.Aspect : purpose == InfrastructureScriptPurpose.VersionCheck ? "version" : "";
        var subject = c.SubjectLabel;
        if (subject == "CAF" && aspect is "naming") aspect = "abbreviation";
        return Regex.Replace($"{subject} {aspect} {suffix}", @"\s+", " ").Trim();
    }

    private static string Scope(List<string> subjects, Classification c)
    {
        var parts = subjects.Select(x => x switch
        {
            ".terraform.lock.hcl" => "provider lock files",
            "required_providers" => "required_providers constraints",
            "module blocks" => "module blocks",
            "Azure Verified Modules (avm-*)" => "Azure/avm-* module blocks",
            "names = { … } blocks" => "names = { … } blocks in .tf files",
            "*.tf" => "Terraform files",
            _ => $"{x} declarations",
        }).Distinct().ToList();
        if (parts.Contains("Azure/avm-* module blocks")) parts.Remove("module blocks");
        if (parts.Count > 1) parts.Remove("Terraform files");
        return parts.Count == 0 ? "Infrastructure source" : string.Join(", ", parts);
    }

    // ── where scripts are configured to run ─────────────────────────────────────────────────────────────────────────

    private static IEnumerable<Invocation> PipelineInvocations(SourceEvidenceContext context, HashSet<string> scriptPaths)
    {
        foreach (var pipeline in context.CiCd?.Pipelines ?? [])
        {
            var deploys = pipeline.Steps.Any(s => s.Kind is PipelineStepKind.InfrastructureDeploy or PipelineStepKind.InfrastructurePlan);
            var infrastructurePipeline = deploys || pipeline.Steps.Any(s => s.Tool.StartsWith("terraform", StringComparison.Ordinal));
            var trigger = pipeline.IsTemplate ? "Template: runs where it is included" : pipeline.Triggers.Count == 0 ? null
                : string.Join("; ", pipeline.Triggers.Select(t => t.Type switch
                {
                    "none" => $"{(t.Disables == "pull-request" ? "PR" : "CI")} trigger disabled",
                    "schedule" => $"schedule {t.Schedule}".Trim(),
                    _ => $"{t.Type}{(t.BranchesInclude.Count > 0 ? $" ({string.Join(", ", t.BranchesInclude.Take(3))})" : "")}",
                }).Distinct().Take(3));
            foreach (var step in pipeline.Steps.Where(s => s.Script is not null))
            {
                var path = step.Targets.FirstOrDefault(t => scriptPaths.Contains(t)) ?? ScriptPathResolver.Resolve(step.Script!.Path, step.WorkingDirectory, scriptPaths);
                if (path is null) continue;
                var tolerated = step.Script!.FailureTolerated || step.ContinueOnError;
                yield return new(new InfrastructureScriptInvocation
                {
                    ScriptId = path, Source = "Pipeline", Reference = pipeline.Id, Pipeline = pipeline.Name, Stage = step.Stage, Job = step.Job, Step = step.Name,
                    Interpreter = step.Script.Interpreter,
                    Blocking = tolerated ? InfrastructureInvocationBlocking.FailureTolerated : InfrastructureInvocationBlocking.Gate,
                    BlockingBasis = step.Script.FailureTolerated ? "The step continues when the script fails (|| …)." : step.ContinueOnError ? "continueOnError: the pipeline continues when the step fails." : "A failing script fails the step.",
                    Trigger = trigger, File = pipeline.File, Line = step.Line,
                }, infrastructurePipeline, $"Run by pipeline {pipeline.Name}{(deploys ? " (which plans/deploys infrastructure)" : infrastructurePipeline ? " (which runs Terraform)" : "")}");
            }
        }
    }

    /// <summary>pre-commit hooks (<c>repo: local</c>) whose entry is a repository script: a commit gate on developer machines, not CI.</summary>
    private static IEnumerable<Invocation> HookInvocations(SourceEvidenceContext context, HashSet<string> scriptPaths)
    {
        foreach (var file in context.Files.Where(f => f.Name is ".pre-commit-config.yaml" or ".pre-commit-config.yml"))
        {
            var root = MiniYaml.Parse(file.Content);
            foreach (var repo in root?.List("repos") ?? [])
                foreach (var hook in repo.List("hooks"))
                {
                    var entry = hook.Str("entry");
                    if (entry is null) continue;
                    var word = Regex.Split(entry.Trim(), @"\s+").FirstOrDefault(w => ScriptPathResolver.IsScript(w)) ?? "";
                    var path = word.Length == 0 ? null : ScriptPathResolver.Resolve(word, null, scriptPaths);
                    if (path is null) continue;
                    var files = hook.Str("files");
                    var tf = files is not null && Regex.IsMatch(files, @"\\?\.tf|tfvars|\.hcl");
                    var id = hook.Str("id") ?? "(hook)";
                    yield return new(new InfrastructureScriptInvocation
                    {
                        ScriptId = path, Source = "Pre-commit hook", Reference = SourceEvidenceRedaction.Safe(id), Step = hook.Str("name") is { } n ? SourceEvidenceRedaction.SafePath(n) : null,
                        Interpreter = hook.Str("language") is { } language ? SourceEvidenceRedaction.Safe(language) : null,
                        Blocking = InfrastructureInvocationBlocking.Gate, BlockingBasis = "pre-commit blocks the commit when the hook exits non-zero (developer machines; not CI).",
                        Trigger = files is null ? "on every commit" : $"on commit of files matching {SourceEvidenceRedaction.SafeText(files)}",
                        File = SourceEvidenceRedaction.Safe(file.Path), Line = hook.Line,
                    }, tf, $"Pre-commit hook '{SourceEvidenceRedaction.Safe(id)}'{(tf ? " on Terraform changes" : "")}");
                }
        }
    }
}
