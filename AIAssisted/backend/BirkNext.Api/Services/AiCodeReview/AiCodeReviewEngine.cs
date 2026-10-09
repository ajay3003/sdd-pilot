using BirkNext.AiCodeReview;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.AiCodeReview;

/// <summary>
/// The AI-Generated Code Review profile: a deterministic rule catalogue over evidence BirkNext already owns. It reads ONE current Source
/// Analysis snapshot (plus an optional baseline of the same repository) and the workspace's SDD-graph summary; it never re-reads source,
/// never calls a language model, never infers authorship and computes no score. Every rule reports how it ran; every finding carries
/// provenance (evidence sources, snapshot ids, file:line or related ids), a limitation and a suggested review action.
/// </summary>
public static class AiCodeReviewEngine
{
    public const int EngineVersion = 1;

    /// <summary>The rule catalogue: id, category, title, evidence sources. Order is display order.</summary>
    public static readonly IReadOnlyList<(string Id, AiCodeReviewCategory Category, string Title, AiEvidenceSource[] Sources)> Catalogue =
    [
        ("AIC-REQ-001", AiCodeReviewCategory.RequirementsAlignment, "Requirements without implementation evidence", [AiEvidenceSource.Specification]),
        ("AIC-REQ-002", AiCodeReviewCategory.RequirementsAlignment, "Completed tasks without implementation evidence", [AiEvidenceSource.Specification]),
        ("AIC-REQ-003", AiCodeReviewCategory.RequirementsAlignment, "Implementation evidence bound to another snapshot", [AiEvidenceSource.Specification, AiEvidenceSource.Source]),
        ("AIC-REQ-004", AiCodeReviewCategory.RequirementsAlignment, "Changed source not linked to a requirement", [AiEvidenceSource.Specification, AiEvidenceSource.SnapshotDiff]),
        ("AIC-ARCH-001", AiCodeReviewCategory.ArchitectureConstitution, "Architecture evidence changed", [AiEvidenceSource.Architecture, AiEvidenceSource.SnapshotDiff]),
        ("AIC-ARCH-002", AiCodeReviewCategory.ArchitectureConstitution, "Explicit architecture / constitution rules", [AiEvidenceSource.Constitution, AiEvidenceSource.Architecture]),
        ("AIC-REF-001", AiCodeReviewCategory.UnresolvedReferences, "Unresolved project namespace import", [AiEvidenceSource.Source, AiEvidenceSource.Dependency]),
        ("AIC-REF-002", AiCodeReviewCategory.UnresolvedReferences, "Configuration key read but not declared", [AiEvidenceSource.Source, AiEvidenceSource.Configuration]),
        ("AIC-REF-003", AiCodeReviewCategory.UnresolvedReferences, "Endpoint referenced in source but absent from contracts", [AiEvidenceSource.Source, AiEvidenceSource.Contract]),
        ("AIC-REF-004", AiCodeReviewCategory.UnresolvedReferences, "Compiler confirmation of unresolved references", [AiEvidenceSource.Build]),
        ("AIC-DUP-001", AiCodeReviewCategory.DuplicateLogic, "Potential duplicate type", [AiEvidenceSource.Source]),
        ("AIC-DUP-002", AiCodeReviewCategory.DuplicateLogic, "Potential duplicate implementation", [AiEvidenceSource.Source]),
        ("AIC-DEP-001", AiCodeReviewCategory.Dependencies, "Introduced or changed dependency", [AiEvidenceSource.Dependency, AiEvidenceSource.SnapshotDiff]),
        ("AIC-DEP-002", AiCodeReviewCategory.Dependencies, "Introduced dependency without usage evidence", [AiEvidenceSource.Dependency, AiEvidenceSource.Source]),
        ("AIC-DEP-003", AiCodeReviewCategory.Dependencies, "Known vulnerability in an introduced dependency", [AiEvidenceSource.Dependency]),
        ("AIC-SEC-001", AiCodeReviewCategory.Security, "Authorization protection removed in source evidence", [AiEvidenceSource.Source, AiEvidenceSource.SnapshotDiff]),
        ("AIC-SEC-002", AiCodeReviewCategory.Security, "Endpoint without authorization metadata among protected siblings", [AiEvidenceSource.Source]),
        ("AIC-SEC-003", AiCodeReviewCategory.Security, "Permissive CORS policy in source", [AiEvidenceSource.Source]),
        ("AIC-SEC-004", AiCodeReviewCategory.Security, "Developer exception page without environment condition", [AiEvidenceSource.Source]),
        ("AIC-SEC-005", AiCodeReviewCategory.Security, "Security Configuration Review findings", [AiEvidenceSource.Configuration, AiEvidenceSource.Runtime]),
        ("AIC-VAL-001", AiCodeReviewCategory.ValidationErrorHandling, "Empty catch block", [AiEvidenceSource.Source]),
        ("AIC-VAL-002", AiCodeReviewCategory.ValidationErrorHandling, "Broad catch returns success", [AiEvidenceSource.Source]),
        ("AIC-VAL-003", AiCodeReviewCategory.ValidationErrorHandling, "Exception details returned to the client", [AiEvidenceSource.Source]),
        ("AIC-VAL-004", AiCodeReviewCategory.ValidationErrorHandling, "Request model without validation among validated siblings", [AiEvidenceSource.Source]),
        ("AIC-TEST-001", AiCodeReviewCategory.Tests, "Potentially weak test (no assertion or empty)", [AiEvidenceSource.Tests, AiEvidenceSource.Source]),
        ("AIC-TEST-002", AiCodeReviewCategory.Tests, "Skipped tests", [AiEvidenceSource.Tests]),
        ("AIC-TEST-003", AiCodeReviewCategory.Tests, "Potential test coverage gap for changed code", [AiEvidenceSource.Tests, AiEvidenceSource.SnapshotDiff]),
        ("AIC-TEST-004", AiCodeReviewCategory.Tests, "Implementation and tests changed together", [AiEvidenceSource.Tests, AiEvidenceSource.SnapshotDiff]),
        ("AIC-TEST-005", AiCodeReviewCategory.Tests, "Executed test results", [AiEvidenceSource.Tests, AiEvidenceSource.Build]),
        ("AIC-TEST-006", AiCodeReviewCategory.Tests, "Tests mirroring the implementation", [AiEvidenceSource.Tests]),
        ("AIC-TEST-007", AiCodeReviewCategory.Tests, "Negative tests for specified error scenarios", [AiEvidenceSource.Specification, AiEvidenceSource.Tests]),
        ("AIC-PLACEHOLDER-001", AiCodeReviewCategory.Placeholders, "NotImplementedException in production code", [AiEvidenceSource.Source]),
        ("AIC-PLACEHOLDER-002", AiCodeReviewCategory.Placeholders, "Stub implementation marked TODO", [AiEvidenceSource.Source]),
        ("AIC-PLACEHOLDER-003", AiCodeReviewCategory.Placeholders, "Placeholder value returned", [AiEvidenceSource.Source]),
        ("AIC-PLACEHOLDER-004", AiCodeReviewCategory.Placeholders, "TODO / FIXME comments", [AiEvidenceSource.Source]),
        ("AIC-DEAD-001", AiCodeReviewCategory.DeadCode, "Unused private method", [AiEvidenceSource.Source]),
        ("AIC-DEAD-002", AiCodeReviewCategory.DeadCode, "Unused registrations, DTOs and unreachable branches", [AiEvidenceSource.Source]),
        ("AIC-CONTRACT-001", AiCodeReviewCategory.ContractDrift, "Contract changed between snapshots", [AiEvidenceSource.Contract, AiEvidenceSource.SnapshotDiff]),
        ("AIC-DRIFT-001", AiCodeReviewCategory.ConfigurationDrift, "Security-relevant configuration changed", [AiEvidenceSource.Configuration, AiEvidenceSource.SnapshotDiff]),
        ("AIC-DRIFT-002", AiCodeReviewCategory.ConfigurationDrift, "Configuration, pipeline and infrastructure changes", [AiEvidenceSource.Configuration, AiEvidenceSource.SnapshotDiff]),
        ("AIC-DOC-001", AiCodeReviewCategory.GeneratedDocumentation, "Generated documentation not refreshed", [AiEvidenceSource.GeneratedDocumentation, AiEvidenceSource.SnapshotDiff]),
        ("AIC-DOC-002", AiCodeReviewCategory.GeneratedDocumentation, "Generated documentation drift candidates", [AiEvidenceSource.GeneratedDocumentation]),
        ("AIC-CHANGE-001", AiCodeReviewCategory.ChangeRisk, "Snapshot change summary", [AiEvidenceSource.SnapshotDiff]),
    ];

    private static readonly string[] SecurityConfigTerms = ["auth", "jwt", "cors", "origin", "bearer", "audience", "authority", "issuer", "scope", "role", "policy", "tenant", "clientid", "https", "certificate"];
    private static readonly string[] NonRuntimePackages = ["Microsoft.NET.Test.Sdk", "xunit", "coverlet", "Microsoft.CodeAnalysis", "StyleCop", "SonarAnalyzer", "Microsoft.EntityFrameworkCore.Design",
        "Microsoft.EntityFrameworkCore.Tools", "Microsoft.SourceLink", "Nerdbank", "MinVer", "GitVersion", "Roslynator", "Meziantou.Analyzer"];

    public static AiCodeReviewResult Review(IqrSourceSnapshot current, IqrSourceSnapshot? baseline, AiCodeReviewRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        var ctx = new Context(current, baseline, request, now);
        ctx.Run(ct);
        return ctx.Result();
    }

    private sealed class Context(IqrSourceSnapshot current, IqrSourceSnapshot? baseline, AiCodeReviewRequest request, DateTimeOffset now)
    {
        private readonly Dictionary<string, AiCodeFinding> findings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AiRuleExecution> rules = new(StringComparer.Ordinal);
        private readonly List<AiChangeArea> changes = [];
        private readonly List<string> limitations = [];
        private readonly CodeRiskSourceEvidence? risk = current.CodeRiskEvidence;
        private readonly CodeRiskSourceEvidence? baselineRisk = baseline?.CodeRiskEvidence;
        private readonly AiWorkspaceEvidence? workspace = request.Workspace;
        private HashSet<string>? changedFiles;   // null = no baseline (entire source)
        private HashSet<string> addedFiles = new(StringComparer.Ordinal);
        private bool ChangeMode => baseline is not null;
        private bool ScopeChanged => ChangeMode && request.Scope == AiReviewScope.ChangedFiles;

        public void Run(CancellationToken ct)
        {
            if (ChangeMode) ComputeChangedFiles();
            if (risk is null) limitations.Add("This snapshot was analyzed before code-risk observations existed; analyze the source again in Source Analysis to assess source-level rules.");
            else limitations.AddRange(risk.Limitations);
            if (risk?.Truncated == true) limitations.Add("Some code-risk observations were capped per kind; counts may be incomplete for this repository.");
            foreach (var language in risk?.Languages.Where(l => !l.Supported) ?? [])
                limitations.Add($"{language.Language}: {language.Files} file(s) present — {language.Reason}");
            ct.ThrowIfCancellationRequested(); Requirements();
            ct.ThrowIfCancellationRequested(); Architecture();
            ct.ThrowIfCancellationRequested(); References();
            ct.ThrowIfCancellationRequested(); Duplicates();
            ct.ThrowIfCancellationRequested(); Dependencies();
            ct.ThrowIfCancellationRequested(); Security();
            ct.ThrowIfCancellationRequested(); Validation();
            ct.ThrowIfCancellationRequested(); Tests();
            ct.ThrowIfCancellationRequested(); Placeholders();
            ct.ThrowIfCancellationRequested(); DeadCode();
            ct.ThrowIfCancellationRequested(); EvidenceDrift();
            ct.ThrowIfCancellationRequested(); GeneratedDocs();
        }

        // ── plumbing ──────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static (string Id, AiCodeReviewCategory Category, string Title, AiEvidenceSource[] Sources) Rule(string id) => Catalogue.First(r => r.Id == id);

        private void State(string id, AiRuleExecutionState state, string? reason = null)
        {
            var r = Rule(id);
            rules[id] = new(id, r.Category, r.Title, state, reason, rules.TryGetValue(id, out var existing) ? existing.Findings : 0, [.. r.Sources]);
        }

        private bool InScope(string file) => !ScopeChanged || changedFiles!.Contains(file);

        /// <summary>One logical finding per LogicalId: a second evidence path adds a reference and sources, never a second finding.</summary>
        private void Add(string ruleId, string logicalId, AiFindingSeverity severity, string title, string description, string rationale, string limitation, string recommendation,
            IEnumerable<AiFindingLocation>? locations = null, IEnumerable<string>? related = null, AiChangeKind? change = null, string? before = null, string? after = null,
            IEnumerable<AiEvidenceSource>? extraSources = null)
        {
            var r = Rule(ruleId);
            var sources = r.Sources.Concat(extraSources ?? []).Distinct().ToList();
            if (findings.TryGetValue(logicalId, out var existing))
            {
                findings[logicalId] = existing with
                {
                    Severity = (AiFindingSeverity)Math.Max((int)existing.Severity, (int)severity), EvidenceSources = existing.EvidenceSources.Union(sources).ToList(),
                    EvidenceReferences = existing.EvidenceReferences + 1, Change = existing.Change ?? change, Baseline = existing.Baseline ?? before, Current = existing.Current ?? after,
                    RelatedIds = existing.RelatedIds.Union(related ?? []).ToList(),
                };
            }
            else
            {
                findings[logicalId] = new AiCodeFinding
                {
                    LogicalId = logicalId, RuleId = ruleId, Category = r.Category, Title = title, Description = description, Rationale = rationale, Severity = severity,
                    EvidenceSources = sources, SourceSnapshotId = current.Id, BaselineSnapshotId = baseline?.Id, Locations = (locations ?? []).Take(50).ToList(),
                    RelatedIds = (related ?? []).Take(200).ToList(), Change = change, Baseline = before, Current = after, Limitation = limitation, Recommendation = recommendation,
                };
            }
            var rule = rules.TryGetValue(ruleId, out var e) ? e : new(ruleId, r.Category, r.Title, AiRuleExecutionState.Executed, null, 0, [.. r.Sources]);
            rules[ruleId] = rule with { Findings = findings.Values.Count(f => f.RuleId == ruleId) };
        }

        private IEnumerable<CodeRiskObservation> Observed(CodeRiskObservationKind kind, bool includeTests = false) =>
            (risk?.Observations ?? []).Where(o => o.Kind == kind && (includeTests || !o.InTestCode) && InScope(o.File));

        /// <summary>Introduced = present now, absent at the baseline location key (file + symbol), when a baseline is selected.</summary>
        private AiChangeKind? ChangeOf(CodeRiskObservation o)
        {
            if (!ChangeMode) return null;
            if (baselineRisk is null) return null;
            return baselineRisk.Observations.Any(b => b.Kind == o.Kind && b.File == o.File && b.Symbol == o.Symbol) ? AiChangeKind.Unchanged : AiChangeKind.Introduced;
        }

        private bool RequireRisk(params string[] ids)
        {
            if (risk is not null) return true;
            foreach (var id in ids) State(id, AiRuleExecutionState.NotAssessed, "The snapshot has no code-risk observations (analyzed before this profile existed); analyze the source again.");
            return false;
        }

        private void ObservationRule(string ruleId, CodeRiskObservationKind kind, AiFindingSeverity severity, string title, string rationale, string limitation, string recommendation,
            bool includeTests = false, Func<CodeRiskObservation, AiFindingSeverity>? severityOf = null)
        {
            if (!RequireRisk(ruleId)) return;
            State(ruleId, AiRuleExecutionState.Executed);
            foreach (var o in Observed(kind, includeTests))
            {
                var change = ChangeOf(o);
                Add(ruleId, $"{kind}|{o.File}|{o.Symbol}|{o.Line}", severityOf?.Invoke(o) ?? severity, title, o.Detail, rationale, limitation, recommendation,
                    [new(o.File, o.Line, o.Symbol)], change: change);
            }
        }

        private void ComputeChangedFiles()
        {
            var before = baseline!.TargetIndex?.Files.GroupBy(f => f.RelativePath).ToDictionary(g => g.Key, g => g.First().ContentFingerprint, StringComparer.Ordinal) ?? [];
            var after = current.TargetIndex?.Files.GroupBy(f => f.RelativePath).ToDictionary(g => g.Key, g => g.First().ContentFingerprint, StringComparer.Ordinal) ?? [];
            changedFiles = new(StringComparer.Ordinal);
            int added = 0, removed = 0, changed = 0, unchanged = 0, unknown = 0;
            foreach (var (path, fingerprint) in after)
            {
                if (!before.TryGetValue(path, out var old)) { added++; changedFiles.Add(path); addedFiles.Add(path); }
                else if (fingerprint is null || old is null) unknown++;
                else if (fingerprint != old) { changed++; changedFiles.Add(path); }
                else unchanged++;
            }
            removed = before.Keys.Count(k => !after.ContainsKey(k));
            changes.Add(new("Files", added, removed, changed, unchanged, unknown > 0 ? $"{unknown} file(s) without a content fingerprint (not read by Source Analysis) are counted as unchanged." : null));
            if (current.TargetIndex is null || baseline.TargetIndex is null) limitations.Add("A snapshot has no file inventory; changed files cannot be determined and the review covers the entire source.");
        }

        // ── Requirements alignment (SDD graph summary from the workspace) ────────────────────────────────────────────────────

        private void Requirements()
        {
            string[] ids = ["AIC-REQ-001", "AIC-REQ-002", "AIC-REQ-003", "AIC-REQ-004"];
            if (workspace is not { SpecificationAvailable: true })
            {
                foreach (var id in ids) State(id, AiRuleExecutionState.NotAssessed, "No Specification is loaded in the current workspace; requirement alignment needs the existing requirement graph.");
                return;
            }
            const string limitation = "Implementation evidence means an explicit link (CodeLink or recorded evidence) — not that the requirement is implemented or verified.";
            State("AIC-REQ-001", AiRuleExecutionState.Executed);
            var missing = workspace.Requirements.Where(r => !r.HasImplementationEvidence).Select(r => r.RequirementId).ToList();
            if (missing.Count > 0)
                Add("AIC-REQ-001", "req|missing-implementation", AiFindingSeverity.Low, $"{missing.Count} of {workspace.RequirementCount} requirement(s) have no implementation evidence",
                    "The requirement graph holds no implementation link for these requirements.", "Requirement → implementation links come from the existing SDD graph (CodeLinks and recorded evidence).",
                    limitation, "Confirm the generated change implements these requirements and record CodeLinks or implementation evidence.", related: missing);

            if (!workspace.TasksAvailable) State("AIC-REQ-002", AiRuleExecutionState.NotAssessed, "No Tasks artifact is loaded.");
            else
            {
                State("AIC-REQ-002", AiRuleExecutionState.Executed, workspace.CompletedTasksWithoutRequirementReferences > 0
                    ? $"{workspace.CompletedTasksWithoutRequirementReferences} completed task(s) reference no requirement and cannot be mapped." : null);
                if (workspace.CompletedTasksWithoutEvidence.Count > 0)
                    Add("AIC-REQ-002", "req|done-tasks-without-evidence", AiFindingSeverity.Low, $"{workspace.CompletedTasksWithoutEvidence.Count} completed task(s) have no implementation evidence",
                        "A checked task box is not implementation evidence; the requirements these tasks reference have no implementation link.",
                        "Task completion state from the Tasks artifact; implementation evidence from the requirement graph.", "Task marked done ≠ implementation verified.",
                        "Review whether the generated change implements these tasks; record implementation evidence.", related: workspace.CompletedTasksWithoutEvidence.Select(t => t.TaskId));
            }

            var linked = workspace.Requirements.Where(r => r.HasImplementationEvidence).ToList();
            if (linked.Count == 0) State("AIC-REQ-003", AiRuleExecutionState.NotApplicable, "No requirement has implementation evidence.");
            else
            {
                State("AIC-REQ-003", AiRuleExecutionState.Executed);
                var stale = linked.Where(r => r.ImplementationSnapshotIds.Count > 0 && !r.ImplementationSnapshotIds.Contains(current.Id.ToString(), StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.RequirementId).ToList();
                if (stale.Count > 0)
                    Add("AIC-REQ-003", "req|stale-implementation", AiFindingSeverity.Info, $"{stale.Count} requirement(s) link implementation evidence from another source snapshot",
                        "The implementation links were recorded against an earlier snapshot than the one reviewed.", "Snapshot ids recorded on implementation evidence vs the reviewed snapshot.",
                        "The linked file may still be correct; this is a currentness signal only.", "Re-bind CodeLinks from the reviewed snapshot in Implementation Evidence Review.", related: stale);
            }

            if (!ChangeMode) State("AIC-REQ-004", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot.");
            else if (linked.Count == 0) State("AIC-REQ-004", AiRuleExecutionState.NotAssessed, "No implementation links exist, so changed files cannot be compared with requirement links.");
            else
            {
                State("AIC-REQ-004", AiRuleExecutionState.Executed);
                var linkedFiles = linked.SelectMany(r => r.ImplementationFiles).Select(f => f.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unlinked = changedFiles!.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsTestPath(f) && !linkedFiles.Contains(f)).Order(StringComparer.Ordinal).ToList();
                if (unlinked.Count > 0)
                    Add("AIC-REQ-004", "req|unlinked-changes", AiFindingSeverity.Info, $"{unlinked.Count} changed source file(s) are not linked to any requirement",
                        "These files changed between the snapshots but no requirement links them.", "Changed files from snapshot fingerprints; links from the requirement graph.",
                        "Not every file needs a requirement link (infrastructure, refactoring).", "Check that each change is intended by a requirement or task.",
                        unlinked.Take(50).Select(f => new AiFindingLocation(f, 0)), change: AiChangeKind.Changed);
            }
        }

        private bool IsTestPath(string path) =>
            risk?.Observations.Any(o => o.File == path && o.InTestCode) == true || path.Split('/').Any(s => s.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) || s is "test" or "tests");

        // ── Architecture / constitution ───────────────────────────────────────────────────────────────────────────────────────

        private void Architecture()
        {
            State("AIC-ARCH-002", AiRuleExecutionState.NotAssessed, workspace?.ConstitutionAvailable == true
                ? "The Constitution is loaded, but its principles are prose: no machine-checkable architecture rule (allowed dependency direction, module pattern) is declared in the evidence BirkNext reads, so none is evaluated."
                : "No Constitution is loaded and no machine-checkable architecture rule is declared; no architecture rule is invented.");
            if (!ChangeMode) { State("AIC-ARCH-001", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot."); return; }
            if (current.Architecture is null || baseline!.Architecture is null) { State("AIC-ARCH-001", AiRuleExecutionState.NotAssessed, "A snapshot has no architecture evidence."); return; }
            State("AIC-ARCH-001", AiRuleExecutionState.Executed);
            var diff = ArchitectureDiff.Compare(baseline.Architecture, current.Architecture);
            changes.Add(new("Architecture", diff.Count(d => d.Kind == ArchitectureChangeKind.Added), diff.Count(d => d.Kind == ArchitectureChangeKind.Removed),
                diff.Count(d => d.Kind == ArchitectureChangeKind.Changed), 0));
            foreach (var area in diff.GroupBy(d => d.Area))
                Add("AIC-ARCH-001", $"arch|{area.Key}", AiFindingSeverity.Info, $"Architecture evidence changed: {area.Count()} {area.Key.ToLowerInvariant()} change(s)",
                    string.Join("; ", area.Take(8).Select(d => $"{d.Kind} {d.Name}")), "Source architecture models of the two snapshots compared by stable keys.",
                    "An architecture change is not a violation; no explicit architecture rule is evaluated.", "Confirm the structural change is intended (new components, dependencies, datastores).",
                    related: area.Select(d => d.Key), change: AiChangeKind.Changed);
        }

        // ── Unresolved references ─────────────────────────────────────────────────────────────────────────────────────────────

        private void References()
        {
            State("AIC-REF-003", AiRuleExecutionState.NotAssessed, "Client-side endpoint references are not extracted from source, so they cannot be matched against contracts.");
            State("AIC-REF-004", AiRuleExecutionState.NotAssessed, "No build evidence is attached to the snapshot; unresolved references are not confirmed by a compiler (build not run = NotVerified).");
            if (!RequireRisk("AIC-REF-001", "AIC-REF-002")) return;
            State("AIC-REF-001", AiRuleExecutionState.Executed);
            var packages = (current.DependencyEvidence?.Dependencies ?? []).Select(d => d.PackageName).Concat((current.DependencyEvidence?.ExternalProjectReferences ?? [])
                .Select(r => Path.GetFileNameWithoutExtension(r.ProjectFileName))).Where(p => p.Length > 0).ToList();
            foreach (var o in Observed(CodeRiskObservationKind.UnresolvedNamespaceImport))
            {
                // A namespace a referenced package or external project may provide is not unresolved.
                if (packages.Any(p => o.Symbol.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) || o.Symbol.Equals(p, StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith(o.Symbol + ".", StringComparison.OrdinalIgnoreCase))) continue;
                Add("AIC-REF-001", $"ref|{o.File}|{o.Symbol}", AiFindingSeverity.Medium, $"Unresolved reference: namespace {o.Symbol}",
                    "Potential generated-code hallucination risk: the import names this repository's own namespace root, but no file in the snapshot declares that namespace and no referenced package or external project provides it.",
                    "Namespace declarations across all C# files; package and external project references from dependency evidence.",
                    "Syntax-level only: generated code, conditional compilation or source outside the archive can declare it. A build is the confirmation.",
                    "Build the change; remove or correct the import if the namespace does not exist.", [new(o.File, o.Line, o.Symbol)], change: ChangeOf(o));
            }

            var keys = (current.EvidenceDomains?.Configuration.Entries ?? []).Select(e => Normalize(e.NormalizedKey.Length > 0 ? e.NormalizedKey : e.Key)).ToList();
            if (keys.Count == 0) { State("AIC-REF-002", AiRuleExecutionState.NotAssessed, "The snapshot has no configuration evidence to compare configuration reads with."); return; }
            State("AIC-REF-002", AiRuleExecutionState.Executed);
            foreach (var read in risk!.ConfigurationKeyReads.Where(r => InScope(r.File)))
            {
                var key = Normalize(read.Symbol);
                if (keys.Any(k => k == key || k.StartsWith(key + ":", StringComparison.Ordinal))) continue;
                Add("AIC-REF-002", $"cfg|{key}", AiFindingSeverity.Info, $"Configuration key \"{read.Symbol}\" is read but not declared in configuration files",
                    "Source reads this key as a literal, and no configuration file in the snapshot declares it.", "Literal configuration reads vs configuration evidence keys.",
                    "Environment variables, Key Vault, user secrets or deployment settings may supply it.", "Confirm the key exists in every environment.", [new(read.File, read.Line, read.Symbol)]);
            }
        }

        private static string Normalize(string key) => key.Replace("__", ":").Trim(':').ToLowerInvariant();

        // ── Duplicates ────────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Duplicates()
        {
            if (!RequireRisk("AIC-DUP-001", "AIC-DUP-002")) return;
            State("AIC-DUP-001", AiRuleExecutionState.Executed); State("AIC-DUP-002", AiRuleExecutionState.Executed);
            foreach (var group in risk!.Duplicates.Where(d => d.Locations.Any(l => InScope(l.File))))
            {
                var type = group.Kind == "Type";
                Add(type ? "AIC-DUP-001" : "AIC-DUP-002", $"dup|{group.Kind}|{group.Fingerprint}", AiFindingSeverity.Low,
                    type ? $"Potential duplicate type ({group.Locations.Count} declarations)" : $"Potential duplicate implementation ({group.Locations.Count} methods)",
                    string.Join(", ", group.Locations.Select(l => l.Symbol).Distinct().Take(6)), group.Description,
                    type ? "Identical shapes can be deliberate (separate contracts per boundary)." : "Identical bodies can be deliberate; similarity is structural, not semantic.",
                    "Consider consolidating if the duplicates serve the same purpose.", group.Locations.Select(l => new AiFindingLocation(l.File, l.Line, l.Symbol)));
            }
        }

        // ── Dependencies ──────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Dependencies()
        {
            State("AIC-DEP-003", AiRuleExecutionState.NotAssessed, "Dependency health (vulnerability) results are not attached to source snapshots; run the dependency health review. No vulnerability is assumed.");
            if (!ChangeMode)
            {
                State("AIC-DEP-001", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot.");
                State("AIC-DEP-002", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot: only introduced dependencies are checked for usage.");
                return;
            }
            if (current.DependencyEvidence is null || baseline!.DependencyEvidence is null)
            {
                State("AIC-DEP-001", AiRuleExecutionState.NotAssessed, "A snapshot has no dependency evidence."); State("AIC-DEP-002", AiRuleExecutionState.NotAssessed, "A snapshot has no dependency evidence.");
                return;
            }
            State("AIC-DEP-001", AiRuleExecutionState.Executed);
            static string Key(DeclaredDependency d) => $"{d.Manager}|{d.PackageName}|{d.OwnerFile}".ToLowerInvariant();
            var before = baseline.DependencyEvidence.Dependencies.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            var after = current.DependencyEvidence.Dependencies.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            var introduced = after.Where(a => !before.ContainsKey(a.Key)).Select(a => a.Value).ToList();
            var removed = before.Where(b => !after.ContainsKey(b.Key)).Select(b => b.Value).ToList();
            var changedVersions = after.Where(a => before.TryGetValue(a.Key, out var b) && b.CurrentValue != a.Value.CurrentValue).Select(a => (Before: before[a.Key], After: a.Value)).ToList();
            changes.Add(new("Dependencies", introduced.Count, removed.Count, changedVersions.Count, after.Count - introduced.Count - changedVersions.Count));
            foreach (var d in introduced)
                Add("AIC-DEP-001", $"dep|{Key(d)}", AiFindingSeverity.Info, $"Added dependency {d.PackageName} {d.CurrentValue}", $"Declared in {d.OwnerFile} ({d.Manager}).",
                    "Declared dependencies of the two snapshots compared by manager, package and owning file.", "Declared ≠ resolved; transitive dependencies are not inventoried.",
                    "Confirm the dependency is needed and approved.", [new(d.OwnerFile, 0, d.PackageName)], change: AiChangeKind.Introduced, after: d.CurrentValue);
            foreach (var (b, a) in changedVersions)
                Add("AIC-DEP-001", $"dep|{Key(a)}", AiFindingSeverity.Info, $"Dependency {a.PackageName} changed version", $"{b.CurrentValue} → {a.CurrentValue} in {a.OwnerFile}.",
                    "Declared versions compared between snapshots.", "Version change compatibility is not assessed here.", "Review release notes for the version change.",
                    [new(a.OwnerFile, 0, a.PackageName)], change: AiChangeKind.Changed, before: b.CurrentValue, after: a.CurrentValue);

            if (risk is null) { State("AIC-DEP-002", AiRuleExecutionState.NotAssessed, "No code-risk usage evidence (namespace imports) in the current snapshot."); return; }
            var nuget = introduced.Where(d => d.Manager.Equals("nuget", StringComparison.OrdinalIgnoreCase)).ToList();
            State("AIC-DEP-002", nuget.Count == 0 && introduced.Count > 0 ? AiRuleExecutionState.Unsupported : AiRuleExecutionState.Executed,
                introduced.Count > nuget.Count ? $"{introduced.Count - nuget.Count} introduced non-NuGet dependenc(ies): usage evidence exists only for C# imports (Unsupported)." : null);
            foreach (var d in nuget.Where(d => !NonRuntimePackages.Any(p => d.PackageName.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                var project = Path.GetDirectoryName(d.OwnerFile.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                // Dependency evidence paths are repository-relative; code-risk paths keep the archive root folder.
                var imports = risk.ProjectUsage.Where(u => u.ProjectPath == project || u.ProjectPath.EndsWith("/" + project, StringComparison.Ordinal))
                    .SelectMany(u => u.ImportedNamespaces).ToList();
                var stem = string.Join('.', d.PackageName.Split('.').Take(2));
                if (imports.Any(n => n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) || d.PackageName.StartsWith(n + ".", StringComparison.OrdinalIgnoreCase) || n.Equals(d.PackageName, StringComparison.OrdinalIgnoreCase))) continue;
                Add("AIC-DEP-002", $"dep-usage|{Key(d)}", AiFindingSeverity.Low, $"Potential unnecessary dependency: {d.PackageName}",
                    $"Added in {d.OwnerFile}, but no C# file of that project imports a namespace of the package.", "Namespace imports per project (code-risk evidence) vs the package id.",
                    "Packages can be used without a namespace import (extension methods in global namespaces, build-time packages, analyzers).",
                    "Remove the dependency if it is unused.", [new(d.OwnerFile, 0, d.PackageName)], change: AiChangeKind.Introduced, extraSources: [AiEvidenceSource.SnapshotDiff]);
            }
        }

        // ── Security ──────────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Security()
        {
            State("AIC-SEC-005", AiRuleExecutionState.NotAssessed, "Security Configuration Review runs per Target Environment; its findings are reviewed there and are not duplicated here.");
            if (!RequireRisk("AIC-SEC-001", "AIC-SEC-002", "AIC-SEC-003", "AIC-SEC-004")) return;
            const string sourceOnly = "Source authorization metadata only — runtime enforcement is not verified (run API Quality Review against a target for that).";
            if (!ChangeMode) State("AIC-SEC-001", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot.");
            else if (baselineRisk is null) State("AIC-SEC-001", AiRuleExecutionState.NotAssessed, "The baseline snapshot has no code-risk observations.");
            else
            {
                State("AIC-SEC-001", AiRuleExecutionState.Executed);
                var now = risk!.Endpoints.ToDictionary(e => e.Key, StringComparer.Ordinal);
                foreach (var before in baselineRisk.Endpoints.Where(e => e.Authorized))
                {
                    if (!now.TryGetValue(before.Key, out var after) || after.Authorized) continue;
                    Add("AIC-SEC-001", $"sec|auth|{before.Key}", AiFindingSeverity.High, $"Authorization protection removed in source evidence: {after.Display}",
                        $"Baseline: {before.Basis}. Current: {after.Basis}.", "Authorization metadata of the same operation compared between snapshots.", sourceOnly,
                        "Restore authorization or document why the operation is now public.", [new(after.File, after.Line, after.Display)], change: AiChangeKind.Changed,
                        before: before.Basis, after: after.Basis, extraSources: [AiEvidenceSource.SnapshotDiff]);
                }
                changes.Add(new("Security controls", risk.Endpoints.Count(e => e.Authorized && baselineRisk.Endpoints.All(b => b.Key != e.Key)),
                    baselineRisk.Endpoints.Count(b => b.Authorized && (!now.TryGetValue(b.Key, out var a) || !a.Authorized)),
                    baselineRisk.Endpoints.Count(b => now.TryGetValue(b.Key, out var a) && a.Authorized != b.Authorized),
                    baselineRisk.Endpoints.Count(b => now.TryGetValue(b.Key, out var a) && a.Authorized == b.Authorized), "Endpoints/operations with authorization metadata."));
            }

            State("AIC-SEC-002", AiRuleExecutionState.Executed);
            if (risk!.Endpoints.Count == 0) State("AIC-SEC-002", AiRuleExecutionState.NotApplicable, "No HTTP or GraphQL operations were found in C# source.");
            foreach (var group in risk.Endpoints.GroupBy(e => GroupOf(e)))
            {
                var protectedCount = group.Count(e => e.Authorized);
                if (protectedCount == 0 || protectedCount == group.Count()) continue;
                foreach (var e in group.Where(e => !e.Authorized && !e.AllowAnonymous && InScope(e.File)))
                    Add("AIC-SEC-002", $"sec|auth|{e.Key}", AiFindingSeverity.Medium, $"No authorization metadata: {e.Display}",
                        $"{protectedCount} of {group.Count()} sibling operation(s) in {group.Key} declare authorization; this one declares neither authorization nor anonymous access.",
                        "Project baseline pattern: sibling operations of the same type.", sourceOnly + " Global policies (fallback policy, gateway) are not visible here.",
                        "Add authorization or an explicit [AllowAnonymous] with a reason.", [new(e.File, e.Line, e.Display)]);
            }

            ObservationRule("AIC-SEC-003", CodeRiskObservationKind.PermissiveCors, AiFindingSeverity.Medium, "Permissive CORS policy in source",
                "AllowAnyOrigin, an always-true origin predicate or a \"*\" origin lets any website call the API from a browser.", "Source configuration only; the deployed policy is verified by API Quality Review.",
                "Restrict origins to the known clients.", severityOf: o => o.Detail.Contains("credentials", StringComparison.Ordinal) ? AiFindingSeverity.High : AiFindingSeverity.Medium);
            ObservationRule("AIC-SEC-004", CodeRiskObservationKind.DeveloperExceptionPageUnconditional, AiFindingSeverity.Medium, "Developer exception page without environment condition",
                "Detailed error pages expose stack traces and internals when enabled outside Development.", "The condition may live in a caller or a configuration switch not visible here.",
                "Guard UseDeveloperExceptionPage with an IsDevelopment() check.");
        }

        private static string GroupOf(CodeEndpointAuthorization e) => e.Key.Split('|') is var p && p.Length > 1 && p[0] != "HTTP" || !e.Key.EndsWith("|minimal", StringComparison.Ordinal)
            ? (p[1].LastIndexOf('.') is var i and > 0 ? p[1][..i] : p[1]) : "minimal API " + e.File;

        // ── Validation / error handling ───────────────────────────────────────────────────────────────────────────────────────

        private void Validation()
        {
            ObservationRule("AIC-VAL-001", CodeRiskObservationKind.EmptyCatch, AiFindingSeverity.Medium, "Empty catch block",
                "A failure is swallowed without handling, logging or a comment explaining why.", "A comment-free empty catch can still be intentional (best-effort cleanup).",
                "Handle, log or rethrow, or document why ignoring the failure is safe.");
            ObservationRule("AIC-VAL-002", CodeRiskObservationKind.BroadCatchReturnsSuccess, AiFindingSeverity.Medium, "Broad catch returns success",
                "Catching every exception and returning a success result hides failures from callers.", "Some fallbacks are designed (cache misses, optional features).",
                "Return an error result or rethrow; reserve success for real success.");
            ObservationRule("AIC-VAL-003", CodeRiskObservationKind.ExceptionDetailReturned, AiFindingSeverity.Medium, "Exception details returned to the client",
                "Exception messages and stack traces can leak internals or personal data to callers.", "The response may be internal-only; values are not inspected (and not stored).",
                "Return a generic problem response and log the details server-side.",
                severityOf: o => o.Detail.Contains("broad catch", StringComparison.Ordinal) ? AiFindingSeverity.Medium : AiFindingSeverity.Low);
            ObservationRule("AIC-VAL-004", CodeRiskObservationKind.InputModelWithoutValidation, AiFindingSeverity.Low, "Request model without validation among validated siblings",
                "Other request models of this project declare validation (DataAnnotations, FluentValidation, IValidatableObject); this one does not.",
                "Validation may happen elsewhere (filters, domain layer).", "Add validation consistent with the sibling request models.");
        }

        // ── Tests ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Tests()
        {
            State("AIC-TEST-006", AiRuleExecutionState.Unsupported, "No AST comparison between tests and implementation exists; mirror-test risk is not assessed rather than guessed from text similarity.");
            State("AIC-TEST-007", AiRuleExecutionState.NotAssessed, "Specified error scenarios are not mapped to tests by the existing evidence; negative-test gaps are not inferred.");
            if (RequireRisk("AIC-TEST-001"))
            {
                State("AIC-TEST-001", AiRuleExecutionState.Executed);
                foreach (var o in (risk!.Observations).Where(o => o.Kind is CodeRiskObservationKind.TestWithoutAssertion or CodeRiskObservationKind.EmptyTest && InScope(o.File)))
                    Add("AIC-TEST-001", $"test|{o.File}|{o.Symbol}", o.Kind == CodeRiskObservationKind.EmptyTest ? AiFindingSeverity.Low : AiFindingSeverity.Info,
                        o.Kind == CodeRiskObservationKind.EmptyTest ? "Potentially weak test: empty test method" : "Potentially weak test: no recognizable assertion",
                        o.Detail, "Test method body inspected for assertion/verification calls and test-class helper calls.",
                        "Assertions in custom helpers outside the test class, or exception-only tests, are not recognized.", "Add an assertion on the expected behaviour.",
                        [new(o.File, o.Line, o.Symbol)], change: ChangeOf(o));
            }

            var inventory = current.TestInventory;
            if (inventory is null) State("AIC-TEST-002", AiRuleExecutionState.NotAssessed, "No test inventory in the snapshot.");
            else
            {
                State("AIC-TEST-002", AiRuleExecutionState.Executed);
                var skipped = inventory.Definitions.Where(d => d.SkipDeclared && InScope(d.FilePath)).ToList();
                if (skipped.Count > 0)
                    Add("AIC-TEST-002", "test|skipped", ChangeMode && skipped.Any(s => changedFiles!.Contains(s.FilePath)) ? AiFindingSeverity.Low : AiFindingSeverity.Info,
                        $"{skipped.Count} test(s) declare Skip in source", "Skipped tests do not run.", "Test definitions discovered from source (Skip declared).",
                        "A source skip is not a result; execution evidence may differ.", "Re-enable or remove skipped tests in the changed area.",
                        skipped.Take(50).Select(s => new AiFindingLocation(s.FilePath, s.Line, s.FullyQualifiedName)));
            }

            if (!ChangeMode)
            {
                State("AIC-TEST-003", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot."); State("AIC-TEST-004", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot.");
            }
            else
            {
                var changedCs = changedFiles!.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
                var prodChanged = changedCs.Where(f => !IsTestPath(f)).ToList();
                var testChanged = changedCs.Where(IsTestPath).ToList();
                changes.Add(new("Tests", testChanged.Count(addedFiles.Contains), 0, testChanged.Count(f => !addedFiles.Contains(f)), 0, "Changed test files (removed files are counted under Files)."));
                State("AIC-TEST-004", AiRuleExecutionState.Executed);
                if (prodChanged.Count > 0 && testChanged.Count > 0)
                    Add("AIC-TEST-004", "test|changed-together", AiFindingSeverity.Info, "Implementation and tests changed together",
                        $"{prodChanged.Count} production and {testChanged.Count} test file(s) changed in the same change.", "Changed files between the snapshots.",
                        "Changing both is normal; it only means the tests are not independent evidence of the old behaviour.",
                        "Review requirement evidence independently of the changed tests.", testChanged.Concat(prodChanged).Take(30).Select(f => new AiFindingLocation(f, 0)), change: AiChangeKind.Changed);
                // Mapping production project → test project only through the naming convention <Project>.Tests / .UnitTests / .IntegrationTests.
                var projects = (inventory?.Projects ?? []).Select(p => Path.GetFileNameWithoutExtension(p.Path)).ToList();
                var mapped = 0;
                State("AIC-TEST-003", AiRuleExecutionState.Executed);
                foreach (var group in prodChanged.GroupBy(ProjectOf).Where(g => g.Key.Length > 0))
                {
                    var name = group.Key.Split('/').Last();
                    var testProjects = projects.Where(p => p.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && Regex(p)).ToList();
                    if (testProjects.Count == 0) continue;
                    mapped++;
                    if (testChanged.Any(t => testProjects.Any(tp => t.Contains("/" + tp + "/", StringComparison.OrdinalIgnoreCase) || t.StartsWith(tp + "/", StringComparison.OrdinalIgnoreCase)))) continue;
                    Add("AIC-TEST-003", $"test|gap|{group.Key}", AiFindingSeverity.Low, $"Potential test coverage gap: {name}",
                        $"{group.Count()} production file(s) changed in {name}, but no file of {string.Join(", ", testProjects)} changed.",
                        "Production → test project mapping by naming convention only.", "Existing tests may already cover the change; no execution or coverage evidence is used.",
                        "Check that tests cover the changed behaviour.", group.Take(20).Select(f => new AiFindingLocation(f, 0)), change: AiChangeKind.Changed);
                }
                if (mapped == 0) State("AIC-TEST-003", AiRuleExecutionState.NotAssessed, "No changed production project maps reliably to a test project by name.");
            }

            var execution = workspace?.TestExecution;
            if (execution is null || execution.Runs == 0) State("AIC-TEST-005", AiRuleExecutionState.NotAssessed, "No executed test results are imported. Test files are not executed tests (NotVerified).");
            else
            {
                State("AIC-TEST-005", AiRuleExecutionState.Executed);
                if (execution.Failed > 0)
                    Add("AIC-TEST-005", "test|failed-results", AiFindingSeverity.Medium, $"Imported test results include {execution.Failed} failed test(s)",
                        $"{execution.Executed} executed, {execution.Passed} passed, {execution.Failed} failed, {execution.Skipped} skipped across {execution.Runs} run(s).",
                        "Imported TRX test-execution evidence.", "The results may predate the reviewed snapshot.", "Fix failing tests before accepting the change.", extraSources: [AiEvidenceSource.Build]);
            }
        }

        private static bool Regex(string project) => System.Text.RegularExpressions.Regex.IsMatch(project, @"\.(Tests?|UnitTests|IntegrationTests|ContractTests)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private string ProjectOf(string file) => risk?.ProjectUsage.Select(u => u.ProjectPath).Where(p => p.Length > 0 && file.StartsWith(p + "/", StringComparison.Ordinal))
            .OrderByDescending(p => p.Length).FirstOrDefault() ?? "";

        // ── Placeholders ──────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Placeholders()
        {
            ObservationRule("AIC-PLACEHOLDER-001", CodeRiskObservationKind.NotImplementedThrow, AiFindingSeverity.Medium, "NotImplementedException in production code",
                "This path fails at runtime when reached.", "The member may be unreachable or intentionally unsupported.", "Implement the member or remove the unreachable path.");
            ObservationRule("AIC-PLACEHOLDER-002", CodeRiskObservationKind.PlaceholderReturn, AiFindingSeverity.Medium, "Stub implementation marked TODO",
                "The member is marked TODO/FIXME and only returns a constant or empty value.", "A constant can be the correct behaviour.", "Implement the behaviour the member's callers expect.");
            ObservationRule("AIC-PLACEHOLDER-003", CodeRiskObservationKind.PlaceholderLiteral, AiFindingSeverity.Medium, "Placeholder value returned",
                "A placeholder-like text (TODO, dummy, not implemented…) is returned to callers.", "The text is matched by a fixed pattern; it may be legitimate data.",
                "Replace the placeholder with real behaviour.");
            if (!RequireRisk("AIC-PLACEHOLDER-004")) return;
            State("AIC-PLACEHOLDER-004", AiRuleExecutionState.Executed);
            var markers = Observed(CodeRiskObservationKind.CommentMarker).ToList();
            if (markers.Count > 0)
                Add("AIC-PLACEHOLDER-004", "placeholder|comments", AiFindingSeverity.Info, $"TODO/FIXME comments in {markers.Count} production file(s)",
                    "Comment-only markers; informational unless executable placeholder code accompanies them (reported separately).",
                    "Comment markers counted per file.", "Comments do not change behaviour.", "Resolve or track the open TODOs.",
                    markers.Take(50).Select(m => new AiFindingLocation(m.File, m.Line, m.Detail)));
        }

        // ── Dead code ─────────────────────────────────────────────────────────────────────────────────────────────────────────

        private void DeadCode()
        {
            State("AIC-DEAD-002", AiRuleExecutionState.Unsupported, "No semantic model: unused service registrations, DTOs and unreachable branches are not determined from syntax alone.");
            ObservationRule("AIC-DEAD-001", CodeRiskObservationKind.UnusedPrivateMethod, AiFindingSeverity.Low, "Unused private method",
                "The private method is not referenced within its type.", "Reflection or source generators can call it.", "Remove the method if it is not needed.");
        }

        // ── Contract and configuration drift (SourceEvidenceDiff) ─────────────────────────────────────────────────────────────

        private void EvidenceDrift()
        {
            string[] ids = ["AIC-CONTRACT-001", "AIC-DRIFT-001", "AIC-DRIFT-002"];
            if (!ChangeMode) { foreach (var id in ids) State(id, AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot of the same repository."); return; }
            if (current.EvidenceDomains is null || baseline!.EvidenceDomains is null) { foreach (var id in ids) State(id, AiRuleExecutionState.NotAssessed, "A snapshot has no source evidence domains."); return; }
            foreach (var id in ids) State(id, AiRuleExecutionState.Executed);
            var diff = SourceEvidenceDiff.Compare(baseline.EvidenceDomains, current.EvidenceDomains);
            foreach (var domain in new[] { SourceEvidenceDomain.Contracts, SourceEvidenceDomain.Configuration, SourceEvidenceDomain.CiCd, SourceEvidenceDomain.Infrastructure })
            {
                var d = diff.Where(c => c.Domain == domain).ToList();
                changes.Add(new(SourceDomainText.Label(domain), d.Count(c => c.Kind == SourceEvidenceChangeKind.Added), d.Count(c => c.Kind == SourceEvidenceChangeKind.Removed),
                    d.Count(c => c.Kind == SourceEvidenceChangeKind.Changed), 0, domain == SourceEvidenceDomain.Contracts ? "Contract changed ≠ contract incompatible ≠ consumer impact." : null));
            }
            foreach (var c in diff.Where(c => c.Domain == SourceEvidenceDomain.Contracts))
            {
                var concern = c.CompatibilityConcern ?? Compatibility(c);
                var severity = concern == ContractCompatibilityConcern.PotentiallyBreaking ? AiFindingSeverity.Medium : AiFindingSeverity.Info;
                Add("AIC-CONTRACT-001", $"contract|{c.Key}", severity, $"Contract {c.Kind.ToString().ToLowerInvariant()}: {c.Name} — {c.Area.ToLowerInvariant()}",
                    $"{c.Detail} Compatibility: {ConcernLabel(concern)}.", "Normalized contract evidence of the two snapshots (operations, types, fields, XSD elements).",
                    "Contract changed ≠ incompatible: consumer impact is not verified. Classification is by change class only.",
                    "Check the specification and known consumers for this change.", related: [c.Key], change: c.Kind switch
                    {
                        SourceEvidenceChangeKind.Added => AiChangeKind.Introduced, SourceEvidenceChangeKind.Removed => AiChangeKind.Removed, _ => AiChangeKind.Changed,
                    });
            }
            var config = diff.Where(c => c.Domain == SourceEvidenceDomain.Configuration).ToList();
            foreach (var c in config.Where(c => SecurityConfigTerms.Any(t => c.Key.Replace("_", "").Contains(t, StringComparison.OrdinalIgnoreCase))))
                Add("AIC-DRIFT-001", $"drift|{c.Key}", c.Kind == SourceEvidenceChangeKind.Removed ? AiFindingSeverity.Medium : AiFindingSeverity.Low,
                    $"Security-relevant configuration {c.Kind.ToString().ToLowerInvariant()}: {c.Name}", c.Detail, "Configuration evidence keys compared between snapshots (values never shown when sensitive).",
                    "Source configuration only; deployed settings may differ.", "Confirm the authentication/CORS/security setting change is intended.",
                    [new(c.Key.Split('|')[0], 0, c.Name)], change: c.Kind == SourceEvidenceChangeKind.Added ? AiChangeKind.Introduced : c.Kind == SourceEvidenceChangeKind.Removed ? AiChangeKind.Removed : AiChangeKind.Changed);
            var other = diff.Where(c => c.Domain is SourceEvidenceDomain.Configuration or SourceEvidenceDomain.CiCd or SourceEvidenceDomain.Infrastructure && !findings.ContainsKey($"drift|{c.Key}")).ToList();
            foreach (var group in other.GroupBy(c => c.Domain))
                Add("AIC-DRIFT-002", $"drift|{group.Key}", AiFindingSeverity.Info, $"{SourceDomainText.Label(group.Key)}: {group.Count()} source change(s)",
                    string.Join("; ", group.Take(10).Select(c => $"{c.Kind} {c.Name}")), SourceEvidenceDiff.Label, "Source evidence change, not runtime or deployment drift.",
                    "Review the listed changes.", related: group.Select(c => c.Key), change: AiChangeKind.Changed);
        }

        /// <summary>Change-class compatibility table (non-XSD): removals and tightened requirements may break consumers; additions are additive.</summary>
        internal static ContractCompatibilityConcern Compatibility(SourceEvidenceChange c) => c.ContractChange switch
        {
            ContractChangeClass.OperationRemoved or ContractChangeClass.FieldRemoved or ContractChangeClass.TypeRemoved or ContractChangeClass.FieldAddedRequired => ContractCompatibilityConcern.PotentiallyBreaking,
            ContractChangeClass.RequirednessChanged => c.Detail.EndsWith("→ required", StringComparison.Ordinal) ? ContractCompatibilityConcern.PotentiallyBreaking : ContractCompatibilityConcern.PotentiallyCompatible,
            ContractChangeClass.TypeChanged => ContractCompatibilityConcern.NeedsReview,
            ContractChangeClass.OperationAdded or ContractChangeClass.FieldAddedOptional or ContractChangeClass.TypeAdded => ContractCompatibilityConcern.PotentiallyCompatible,
            _ => c.Kind == SourceEvidenceChangeKind.Removed ? ContractCompatibilityConcern.PotentiallyBreaking : c.Kind == SourceEvidenceChangeKind.Added ? ContractCompatibilityConcern.PotentiallyCompatible : ContractCompatibilityConcern.NeedsReview,
        };

        private static string ConcernLabel(ContractCompatibilityConcern c) => c switch
        {
            ContractCompatibilityConcern.PotentiallyBreaking => "potentially incompatible for existing consumers (not verified)",
            ContractCompatibilityConcern.PotentiallyCompatible => "additive / potentially compatible",
            _ => "needs review",
        };

        // ── Generated documentation ───────────────────────────────────────────────────────────────────────────────────────────

        private void GeneratedDocs()
        {
            var docs = current.GeneratedDocumentation;
            if (docs is null || docs.Documents.Count == 0)
            {
                State("AIC-DOC-001", AiRuleExecutionState.NotApplicable, "No generated documentation in the snapshot."); State("AIC-DOC-002", AiRuleExecutionState.NotApplicable, "No generated documentation in the snapshot.");
                return;
            }
            State("AIC-DOC-002", AiRuleExecutionState.Executed);
            var open = docs.Drift.Where(d => d.ReviewRequired).ToList();
            if (open.Count > 0)
                Add("AIC-DOC-002", "doc|drift", AiFindingSeverity.Info, $"{open.Count} generated-documentation drift candidate(s) need review",
                    string.Join("; ", open.Take(6).Select(d => d.Explanation)), "Generated-documentation evidence (structured comparisons with source).",
                    "Generated documentation is not truth; a drift candidate is a difference, not a defect.", "Review the candidates in Source Analysis → Generated documentation.",
                    open.SelectMany(d => d.LeftEvidence.Concat(d.RightEvidence)).Where(e => e.Path.Length > 0).Take(30).Select(e => new AiFindingLocation(e.Path, e.Line, e.Label)), related: open.Select(d => d.Id));

            if (!ChangeMode) { State("AIC-DOC-001", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot."); return; }
            State("AIC-DOC-001", AiRuleExecutionState.Executed);
            foreach (var module in docs.Modules.Where(m => m.GeneratedDirectory.Length > 0))
            {
                var root = module.RootPath.Length == 0 ? "" : module.RootPath.TrimEnd('/') + "/";
                var sourceChanged = changedFiles!.Any(f => f.StartsWith(root, StringComparison.Ordinal) && f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !f.StartsWith(module.GeneratedDirectory, StringComparison.Ordinal));
                var docsChanged = changedFiles!.Any(f => f.StartsWith(module.GeneratedDirectory.TrimEnd('/') + "/", StringComparison.Ordinal));
                if (sourceChanged && !docsChanged)
                    Add("AIC-DOC-001", $"doc|stale|{module.ModuleId}", AiFindingSeverity.Low, $"Source changed but generated documentation did not: {module.DisplayName}",
                        $"C# files under {module.RootPath} changed; nothing under {module.GeneratedDirectory} changed.", "Changed files between snapshots vs the module's generated-documentation directory.",
                        "Only content-fingerprinted files are compared; documentation may not need regeneration for every change.", "Regenerate the module documentation if the change affects it.",
                        [new(module.GeneratedDirectory, 0, module.DisplayName)], change: AiChangeKind.Unchanged);
            }
        }

        // ── Result ────────────────────────────────────────────────────────────────────────────────────────────────────────────

        public AiCodeReviewResult Result()
        {
            foreach (var r in Catalogue.Where(r => !rules.ContainsKey(r.Id))) State(r.Id, AiRuleExecutionState.NotAssessed, "Not evaluated.");
            var ordered = Catalogue.Select(c => rules[c.Id]).ToList();
            var list = findings.Values.OrderByDescending(f => f.Severity).ThenBy(f => f.Category).ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ThenBy(f => f.Locations.FirstOrDefault()?.File, StringComparer.Ordinal).ThenBy(f => f.Locations.FirstOrDefault()?.Line).ToList();
            var categories = Enum.GetValues<AiCodeReviewCategory>().Where(c => c != AiCodeReviewCategory.ChangeRisk).Select(c => Category(c, ordered, list)).ToList();

            AiReviewAttention? attention = null; string? basis = null;
            if (ChangeMode)
            {
                State("AIC-CHANGE-001", AiRuleExecutionState.Executed);
                var introduced = list.Where(f => f.Change is AiChangeKind.Introduced or AiChangeKind.Changed or AiChangeKind.Removed).ToList();
                attention = introduced.Any(f => f.Severity == AiFindingSeverity.High) ? AiReviewAttention.High
                    : introduced.Any(f => f.Severity == AiFindingSeverity.Medium) ? AiReviewAttention.Moderate : AiReviewAttention.Low;
                basis = "High = a High-severity finding in the change; Moderate = a Medium-severity finding in the change; otherwise Low. Derived only from finding severities — not a score.";
            }
            else State("AIC-CHANGE-001", AiRuleExecutionState.NotApplicable, "Needs a baseline snapshot.");
            ordered = Catalogue.Select(c => rules[c.Id]).ToList();
            categories.Add(ChangeMode
                ? new(AiCodeReviewCategory.ChangeRisk, AiCategoryStatus.Findings, list.Count(f => f.Change is not null and not AiChangeKind.Unchanged), $"{changedFiles?.Count ?? 0} changed file(s)", 0,
                    $"Review attention: {attention}. {basis}")
                : new(AiCodeReviewCategory.ChangeRisk, AiCategoryStatus.NotApplicable, 0, "—", 0, "Select a baseline snapshot of the same repository to review a change."));

            return new AiCodeReviewResult
            {
                RunId = Guid.NewGuid(), Mode = ChangeMode ? AiReviewMode.SnapshotChange : AiReviewMode.CurrentSnapshot, Scope = ScopeChanged ? AiReviewScope.ChangedFiles : AiReviewScope.EntireSource,
                StartedAt = now, CompletedAt = DateTimeOffset.UtcNow, EngineVersion = EngineVersion, ProjectName = workspace?.ProjectName,
                Current = Provenance(current), Baseline = baseline is null ? null : Provenance(baseline), Readiness = Readiness(),
                Categories = categories, Rules = ordered, Findings = list, Changes = changes, ChangedFiles = changedFiles?.Count ?? 0,
                Attention = attention, AttentionBasis = basis, Limitations = limitations.Distinct().ToList(),
            };
        }

        private AiCategoryResult Category(AiCodeReviewCategory category, List<AiRuleExecution> ordered, List<AiCodeFinding> list)
        {
            var categoryRules = ordered.Where(r => r.Category == category).ToList();
            var executed = categoryRules.Where(r => r.State == AiRuleExecutionState.Executed).ToList();
            var count = list.Count(f => f.Category == category);
            var unsupported = categoryRules.Count(r => r.State == AiRuleExecutionState.Unsupported);
            var status = count > 0 ? AiCategoryStatus.Findings
                : executed.Count > 0 ? AiCategoryStatus.NoIndicators
                : categoryRules.All(r => r.State == AiRuleExecutionState.NotApplicable) ? AiCategoryStatus.NotApplicable
                : categoryRules.All(r => r.State is AiRuleExecutionState.Unsupported or AiRuleExecutionState.NotApplicable) ? AiCategoryStatus.Unsupported
                : AiCategoryStatus.NotAssessed;
            var scope = ScopeChanged ? $"{changedFiles!.Count} changed file(s)" : risk is null ? "snapshot metadata" : $"{risk.ProductionFiles} production and {risk.TestFiles} test C# file(s)";
            var explanation = status switch
            {
                AiCategoryStatus.NoIndicators => $"{AiCodeReviewText.NoIndicators} ({executed.Count} of {categoryRules.Count} rule(s) executed)",
                AiCategoryStatus.Findings => $"{executed.Count} of {categoryRules.Count} rule(s) executed.",
                _ => categoryRules.Select(r => r.Reason).FirstOrDefault(r => r is not null),
            };
            return new(category, status, count, scope, unsupported, explanation);
        }

        private List<AiReadinessItem> Readiness() =>
        [
            new("source", "Source snapshot", true, $"{current.Archive.FileName} · {ReviewSourceEvidenceProvider.ShortFingerprint(current)}"),
            new("code-risk", "Code-risk observations", risk is not null, risk is null ? "Snapshot predates the code-risk analyzer; analyze the source again." : $"{risk.ProductionFiles + risk.TestFiles} C# file(s) analyzed", "Source-level rules"),
            new("baseline", "Baseline snapshot", baseline is not null, baseline is null ? "Not selected — change rules are not applicable." : $"{baseline.Archive.FileName} · {ReviewSourceEvidenceProvider.ShortFingerprint(baseline)}", "Change, contract and configuration drift rules"),
            new("specification", "Specification", workspace?.SpecificationAvailable == true, workspace?.SpecificationAvailable == true ? $"{workspace.RequirementCount} requirement(s)" : "Not loaded", "Requirements alignment"),
            new("constitution", "Constitution", workspace?.ConstitutionAvailable == true, workspace?.ConstitutionAvailable == true ? "Loaded (prose principles are not evaluated as code rules)" : "Not loaded", "Constitution rules"),
            new("dependencies", "Dependency evidence", current.DependencyEvidence is not null, current.DependencyEvidence is null ? "Not captured" : $"{current.DependencyEvidence.Dependencies.Count} declared dependenc(ies)", "Dependency rules"),
            new("contracts", "Contracts", current.EvidenceDomains?.Contracts.Contracts.Count > 0, $"{current.EvidenceDomains?.Contracts.Contracts.Count ?? 0} contract(s)", "Contract drift"),
            new("tests", "Tests discovered", current.TestInventory?.Definitions.Count > 0, $"{current.TestInventory?.Definitions.Count ?? 0} test definition(s)", "Test rules"),
            new("execution", "Build / test execution evidence", workspace?.TestExecution is { Runs: > 0 }, workspace?.TestExecution is { Runs: > 0 } t ? $"{t.Runs} imported run(s)" : "None imported (NotVerified)", "Execution rules"),
        ];
    }

    public static AiSnapshotProvenance Provenance(IqrSourceSnapshot s) =>
        new(s.Id, ReviewSourceEvidenceProvider.Identity(s).DisplayName, s.Archive.FileName, s.Archive.Sha256, s.AnalyzedAt, s.CodeRiskEvidence is not null);
}
