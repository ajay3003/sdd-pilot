using System.Security.Cryptography;
using System.Text;
using BirkNext.ImplementationGaps;

namespace BirkNext.Api.Services.ImplementationGaps;

/// <summary>
/// Pure, deterministic implementation-gap evaluation (rules <see cref="GapRules"/>): SDD inputs × source evidence × recorded executions → task
/// assessments (task state, implementation evidence and verification evidence kept apart), gate assessments, feature readiness with explicit
/// dimensions, and explainable findings with stable ids. No I/O, no clock (the caller passes <c>now</c>), no keyword similarity: an artifact is
/// Confirmed only where the task says it is, Candidate when found elsewhere by exact name, Ambiguous when several candidates exist, Missing when
/// looked for everywhere, Unresolved when the inventory cannot decide.
/// </summary>
public static class ImplementationGapEngine
{
    public static GapEvaluation Evaluate(GapDocumentSet docs, GapSourceEvidence? source, IReadOnlyList<GapExecutedTest> executions,
        IReadOnlyList<GapRuntimeObservation> runtime, DateTimeOffset now, string scopeKey)
    {
        var index = source is null ? null : new SourceIndex(source);
        var gates = docs.Gates.Select(g => AssessGate(g, docs)).ToList();
        var gateByTask = new Dictionary<string, List<GapGateAssessment>>(StringComparer.Ordinal);
        foreach (var (input, assessed) in docs.Gates.Zip(gates))
            foreach (var t in assessed.BlockedTaskIds)
                (gateByTask.TryGetValue(Key(input.Source.Path, t), out var l) ? l : gateByTask[Key(input.Source.Path, t)] = []).Add(assessed);

        var tasks = docs.Tasks.Select(t => AssessTask(t, docs, index, gateByTask.GetValueOrDefault(Key(t.DocumentPath, t.TaskId)) ?? [])).ToList();
        var byKey = tasks.Zip(docs.Tasks).ToDictionary(p => Key(p.Second.DocumentPath, p.Second.TaskId), p => p.First, StringComparer.Ordinal);
        // Verification needs every task's artifacts first: test tasks are associated by explicit task-id references only.
        tasks = [.. tasks.Zip(docs.Tasks).Select(p => p.First with { Verification = Verification(p.Second, p.First, docs, byKey, index, executions, runtime, out var testIds), TestTaskIds = testIds })];
        byKey = tasks.Zip(docs.Tasks).ToDictionary(p => Key(p.Second.DocumentPath, p.Second.TaskId), p => p.First, StringComparer.Ordinal);

        var features = Features(docs, gates, byKey, index, runtime);
        var findings = Findings(docs, source, index, tasks, gates, features, now);
        var limitations = new List<string>(docs.Limitations);
        if (source is null) limitations.Add("No source snapshot: implementation and verification evidence are Not assessed.");
        else limitations.AddRange(source.Limitations.Take(10));
        if (executions.Count == 0) limitations.Add("No recorded test execution was supplied: test execution coverage is Not assessed.");
        if (runtime.Count == 0) limitations.Add("No runtime verification was supplied: runtime execution is Not assessed.");
        return new GapEvaluation
        {
            ScopeKey = scopeKey, SnapshotId = source?.SnapshotId, ArchiveName = source?.ArchiveName, ArchiveSha256 = source?.ArchiveSha256, EvaluatedAt = now,
            RuleSetVersion = GapRules.RuleSetVersion, Documents = docs.Documents, TaskCount = tasks.Count, CompletedTaskCount = tasks.Count(t => t.TaskState == GapTaskState.Completed),
            Tasks = tasks, Gates = gates, Features = features, Findings = findings,
            ExecutionSources = [.. executions.Select(e => e.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Limitations = limitations,
        };
    }

    private static string Key(string doc, string taskId) => doc + "#" + taskId;

    // ── Source index ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SourceIndex
    {
        public readonly GapSourceEvidence Evidence;
        public readonly HashSet<string> Files;
        public readonly Dictionary<string, List<string>> ByFileName;
        public readonly Dictionary<string, List<GapSymbol>> Symbols;
        public readonly Dictionary<string, List<GapPackage>> Packages;
        public readonly List<string> SortedFiles;

        public SourceIndex(GapSourceEvidence e)
        {
            Evidence = e;
            SortedFiles = [.. e.Files.Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            Files = new HashSet<string>(SortedFiles, StringComparer.OrdinalIgnoreCase);
            ByFileName = SortedFiles.GroupBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            Symbols = e.Symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            Packages = e.Packages.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Any implementation file under the prefix: SDD material (specs/, .specify/) never counts as implementation being present.</summary>
        public bool AnyUnder(string prefix) => SortedFiles.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !IsSddMaterial(f[prefix.Length..]));

        private static bool IsSddMaterial(string rest) =>
            rest.StartsWith("specs/", StringComparison.OrdinalIgnoreCase) || rest.StartsWith(".specify/", StringComparison.OrdinalIgnoreCase)
            || rest.Contains("/specs/", StringComparison.OrdinalIgnoreCase) || rest.Contains("/.specify/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    /// <summary>The scope a path declares its implementation in: the module folder up to and including its first src/ or tests/ segment
    /// (Skole/src/…/X.cs → "Skole/src/"); otherwise its top folder.</summary>
    public static string ScopeOf(string path)
    {
        var parts = Norm(path).Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] is "src" or "tests" or "test" or "source") return string.Join('/', parts[..(i + 1)]) + "/";
        // Otherwise the file's own folder (e.g. "SkoleAdapter/.pipeline/").
        return parts.Length > 1 ? string.Join('/', parts[..^1]) + "/" : "";
    }

    private static List<string> Resolutions(string expected, string documentPath)
    {
        var p = Norm(expected);
        var root = ImplementationGapExtractor.ModuleRoot(documentPath);
        return root.Length > 0 && !p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? [p, root + p] : [p];
    }

    // ── Gates ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static GapGateAssessment AssessGate(GapGateInput g, GapDocumentSet docs)
    {
        var evidence = new List<GapSourceRef> { g.Source };
        GapGateStatus status; string reason;
        var entries = g.DecisionIds.Select(id => (Id: id, Entry: docs.Decisions.FirstOrDefault(d => d.Id == id))).ToList();
        if (g.DecisionIds.Count == 0)
        {
            status = GapGateStatus.Unknown;
            reason = $"The gate \"{g.Label}\" names no decision record, and no approval of it is recorded in the supplied documents.";
        }
        else if (entries.All(e => e.Entry is not null))
        {
            evidence.AddRange(entries.Select(e => e.Entry!.Source));
            status = entries.Select(e => e.Entry!.Status).Aggregate(Worst);
            reason = string.Join(" ", entries.Select(e => $"{e.Id} is recorded with status \"{e.Entry!.StatusText}\" ({e.Entry.Source.Path}:{e.Entry.Source.Line})."));
        }
        else if (docs.DecisionRegisters.Count > 0)
        {
            // A documented, mandatory gate whose decision the register does not contain has no recorded approval: pending sign-off.
            status = GapGateStatus.Pending;
            var missing = entries.Where(e => e.Entry is null).Select(e => e.Id).ToList();
            reason = $"{g.Source.Path}:{g.Source.Line} requires {string.Join(", ", missing)}; the decision register ({string.Join(", ", docs.DecisionRegisters)}) contains no entry for "
                + $"{(missing.Count == 1 ? "it" : "them")} (last recorded: {docs.Decisions.Select(d => d.Id).DefaultIfEmpty("none").Max(StringComparer.Ordinal)}). No approval is recorded.";
            foreach (var t in docs.Tasks.Where(t => missing.Any(m => docs.DecisionTitles.ContainsKey(m)) && t.Text.Contains("Draft", StringComparison.Ordinal) && missing.Any(m => t.DecisionIds.Contains(m))))
                evidence.Add(new(t.DocumentPath, t.Line, $"{t.TaskId} drafts the decision ({t.State})"));
        }
        else
        {
            status = GapGateStatus.Unknown;
            reason = $"{string.Join(", ", g.DecisionIds)} is required by {g.Source.Path}:{g.Source.Line}, but no decision register was supplied, so its status cannot be read.";
        }
        return new GapGateAssessment { GateId = g.GateId, Label = g.Label, Status = status, BlockedTaskIds = g.BlockedTaskIds, Evidence = evidence, Reason = reason };
    }

    private static GapGateStatus Worst(GapGateStatus a, GapGateStatus b) => Rank(a) >= Rank(b) ? a : b;
    private static int Rank(GapGateStatus s) => s switch { GapGateStatus.Approved => 0, GapGateStatus.Pending => 2, GapGateStatus.Unknown => 1, GapGateStatus.Rejected => 3, _ => 1 };

    // ── Tasks ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] ModifyVerbs = ["Add ", "Register ", "Map ", "Replace ", "Extend ", "Update ", "Keep ", "Wire ", "Configure "];

    private static GapTaskAssessment AssessTask(GapTaskInput t, GapDocumentSet docs, SourceIndex? index, List<GapGateAssessment> gates)
    {
        var checks = new List<GapArtifactCheck>();
        var codePaths = t.Paths.Where(ImplementationGapExtractor.IsCodePath).ToList();
        var draftsDecision = t.Text.Contains("Draft", StringComparison.Ordinal) && t.DecisionIds.Count > 0 && docs.DecisionTitles.Keys.Any(t.DecisionIds.Contains);
        var moduleRoot = ImplementationGapExtractor.ModuleRoot(t.DocumentPath);
        var scopes = codePaths.Select(p => ScopeOf(Resolutions(p, t.DocumentPath)[^1])).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A task that names types/packages but no path declares its module as the scope.
        if (scopes.Count == 0 && moduleRoot.Length > 0 && (t.DeclaredTypes.Count > 0 || t.Packages.Count > 0)) scopes = [moduleRoot];
        var scopeAbsent = index is not null && index.Evidence.FileInventoryComplete && scopes.Count > 0 && scopes.All(s => !index.AnyUnder(s));
        var resolvedFiles = new List<string>();

        if (index is not null)
        {
            if (draftsDecision)
                foreach (var id in t.DecisionIds.Where(docs.DecisionTitles.ContainsKey))
                {
                    var entry = docs.Decisions.FirstOrDefault(d => d.Id == id);
                    checks.Add(new GapArtifactCheck
                    {
                        Kind = GapArtifactKind.File, Expected = $"{id} entry in the decision register", Rule = "decision-register-entry",
                        Relationship = docs.DecisionRegisters.Count == 0 ? GapRelationship.Unresolved : entry is null ? GapRelationship.Missing : GapRelationship.Confirmed,
                        Actual = entry is null ? [] : [entry.Source],
                        Detail = docs.DecisionRegisters.Count == 0 ? "No decision register was supplied." : entry is null ? $"{string.Join(", ", docs.DecisionRegisters)} has no {id} entry." : $"Recorded as \"{entry.StatusText}\".",
                    });
                }
            foreach (var p in codePaths) checks.Add(FileCheck(p, t, index, resolvedFiles));
            foreach (var type in t.DeclaredTypes) checks.Add(TypeCheck(type, index, resolvedFiles, namesFile: codePaths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))));
            // The project a package is pinned to: where its .csproj check found it (exact, or a single moved candidate); none found = the project is
            // absent, and a declaration by some other project is no evidence for this task.
            var projectCheck = checks.FirstOrDefault(c => c.Expected.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            var projectFiles = projectCheck is null ? null
                : projectCheck.Relationship is GapRelationship.Confirmed or GapRelationship.Candidate ? projectCheck.Actual.Select(a => a.Path).ToList() : [];
            foreach (var package in t.Packages) checks.Add(PackageCheck(package, projectFiles, index));
        }

        GapImplementationEvidence impl;
        string explanation;
        // Required evidence: declared types/packages/decision entries when the task names any; otherwise the code files it names. For a task that
        // modifies an existing file (Add/Register/Map … in `X`), the file existing cannot show the change: at most Candidate.
        var required = checks.Where(c => c.Kind is GapArtifactKind.Type or GapArtifactKind.Package || c.Rule == "decision-register-entry").ToList();
        var fileOnly = required.Count == 0;
        if (fileOnly) required = [.. checks.Where(c => c.Kind is GapArtifactKind.File or GapArtifactKind.Test)];
        var modifies = fileOnly && ModifyVerbs.Any(v => TaskLead(t.Text).StartsWith(v, StringComparison.Ordinal));

        if (index is null) { impl = GapImplementationEvidence.NotAssessed; explanation = "No source snapshot: implementation not assessed."; }
        else if (required.Count == 0) { impl = GapImplementationEvidence.NotAssessed; explanation = "The task names no source artifact (file, type, package or decision), so its implementation cannot be assessed from source."; }
        else
        {
            var confirmed = required.Count(c => c.Relationship == GapRelationship.Confirmed);
            var found = required.Count(c => c.Relationship is GapRelationship.Candidate or GapRelationship.Ambiguous);
            var missing = required.Count(c => c.Relationship == GapRelationship.Missing);
            var unresolved = required.Count(c => c.Relationship == GapRelationship.Unresolved);
            impl = confirmed == required.Count ? (modifies ? GapImplementationEvidence.CandidateImplementation : GapImplementationEvidence.ConfirmedSourceImplementation)
                : confirmed > 0 ? GapImplementationEvidence.PartialImplementation
                : found > 0 ? GapImplementationEvidence.CandidateImplementation
                : missing > 0 && unresolved == 0 ? GapImplementationEvidence.NoMatchingSource
                : missing > 0 ? GapImplementationEvidence.NoMatchingSource
                : GapImplementationEvidence.NotAssessed;
            explanation = scopeAbsent && confirmed + found == 0
                ? $"Implementation not evidenced in supplied archive: no file under {string.Join(", ", scopes)} is in it (the archive does not show whether it exists elsewhere)."
                : $"{confirmed} of {required.Count} expected artifact(s) confirmed at the stated location, {found} found elsewhere or ambiguous, {missing} not found, {unresolved} undecidable."
                  + (modifies && confirmed == required.Count ? " The file exists, but the change the task describes inside it cannot be verified from the inventory." : "");
        }
        return new GapTaskAssessment
        {
            TaskId = t.TaskId, Source = new(t.DocumentPath, t.Line), Text = t.Text, TaskState = t.State, Implementation = impl, ScopeAbsentFromArchive = scopeAbsent && impl == GapImplementationEvidence.NoMatchingSource,
            DeclaredScopes = scopes,
            Artifacts = checks, GateIds = [.. gates.Select(g => g.GateId).Distinct()], RequirementIds = t.RequirementIds, IsTestTask = t.IsTestTask, NotApplicableReason = t.NotApplicableReason,
            Explanation = t.NotApplicableReason is { } na ? $"Not applicable to readiness ({na}). {explanation}" : explanation,
        };
    }

    /// <summary>The task's instruction without checkbox, id, [P]/[USn] tags and a leading bold marker ("**Gate: …**").</summary>
    public static string TaskLead(string text)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*[-*+]\s+\[[^\]]\]\s+\**T\d+[a-zA-Z]?\**\s*", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^(?:\[[^\]]+\]\s*)+", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"^\*\*[^*]+\*\*\s*", "");
    }

    private static GapArtifactCheck FileCheck(string expected, GapTaskInput t, SourceIndex index, List<string> resolvedFiles)
    {
        var isTest = expected.Contains("/tests/", StringComparison.OrdinalIgnoreCase) || expected.EndsWith("Tests.cs", StringComparison.Ordinal);
        var kind = isTest ? GapArtifactKind.Test : GapArtifactKind.File;
        foreach (var candidate in Resolutions(expected, t.DocumentPath))
            if (index.Files.Contains(candidate))
            {
                resolvedFiles.Add(candidate);
                var defs = isTest && index.Evidence.TestsCaptured ? index.Evidence.Tests.Count(d => Norm(d.FilePath).Equals(candidate, StringComparison.OrdinalIgnoreCase)) : (int?)null;
                return new GapArtifactCheck
                {
                    Kind = kind, Expected = expected, Relationship = GapRelationship.Confirmed, Actual = [new(candidate)], Rule = "exact-path",
                    Detail = defs is { } n ? $"Present; {n} test definition(s) discovered in it." : "Present at the stated path.",
                };
            }
        var name = System.IO.Path.GetFileName(expected);
        // A same-named file counts only inside the task's module: another module's Program.cs is never this task's moved file.
        var root = ImplementationGapExtractor.ModuleRoot(t.DocumentPath);
        if (index.ByFileName.TryGetValue(name, out var same)
            && (root.Length > 0 ? same.Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList() : same) is { Count: > 0 } pick)
        {
            return new GapArtifactCheck
            {
                Kind = kind, Expected = expected, Relationship = pick.Count == 1 ? GapRelationship.Candidate : GapRelationship.Ambiguous, Actual = [.. pick.Take(10).Select(f => new GapSourceRef(f))],
                Rule = "same-file-name", Detail = pick.Count == 1 ? $"Not at the stated path; a file with the same name is at {pick[0]} (moved or renamed?)." : $"{pick.Count} files share the name {name}; a person must choose.",
            };
        }
        if (!index.Evidence.FileInventoryComplete)
            return new GapArtifactCheck { Kind = kind, Expected = expected, Relationship = GapRelationship.Unresolved, Rule = "inventory-incomplete", Detail = "The file inventory is incomplete: absence cannot be concluded." };
        return new GapArtifactCheck
        {
            Kind = kind, Expected = expected, Relationship = GapRelationship.Missing, Rule = "exact-path+module-path+same-file-name",
            Detail = $"Not at {string.Join(" or ", Resolutions(expected, t.DocumentPath))}, and no file named {name} anywhere in the archive.",
        };
    }

    private static GapArtifactCheck TypeCheck(string type, SourceIndex index, List<string> resolvedFiles, bool namesFile)
    {
        if (!index.Evidence.SymbolsCaptured)
            return new GapArtifactCheck { Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Unresolved, Rule = "symbols-not-captured", Detail = "The snapshot has no declared-type inventory (re-analyze the source)." };
        if (!index.Symbols.TryGetValue(type, out var found))
        {
            if (index.Evidence.SymbolsTruncated)
                return new GapArtifactCheck { Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Unresolved, Rule = "symbols-truncated", Detail = "The declared-type inventory was truncated: absence cannot be concluded." };
            // Renamed in place: the file the task names exists and declares other types — a candidate for a person, never "missing".
            var inNamedFile = index.Evidence.Symbols.Where(s => resolvedFiles.Contains(Norm(s.File), StringComparer.OrdinalIgnoreCase)).ToList();
            if (inNamedFile.Count > 0)
                return new GapArtifactCheck
                {
                    Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Candidate, Rule = "renamed-type-candidate",
                    Actual = [.. inNamedFile.Take(5).Select(s => new GapSourceRef(Norm(s.File), s.Line > 0 ? s.Line : null, $"{s.Kind} {s.Namespace}.{s.Name}"))],
                    Detail = $"No type named {type}, but the file the task names declares {string.Join(", ", inNamedFile.Take(5).Select(s => s.Name))} (renamed?).",
                };
            return new GapArtifactCheck { Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Missing, Rule = "declared-type", Detail = $"No type named {type} is declared in any analyzed file." };
        }
        var actual = found.Select(s => new GapSourceRef(Norm(s.File), s.Line > 0 ? s.Line : null, $"{s.Kind} {s.Namespace}.{s.Name}")).ToList();
        if (found.All(s => IsGenerated(s.File)))
            return new GapArtifactCheck { Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Candidate, Actual = actual, Rule = "generated-source", Detail = "Declared only in generated source: confirm the generator is the intended implementation." };
        if (found.Any(s => resolvedFiles.Contains(Norm(s.File), StringComparer.OrdinalIgnoreCase)))
            return new GapArtifactCheck { Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Confirmed, Actual = actual, Rule = "declared-type-at-path", Detail = "Declared in the file the task names." };
        var namespaces = found.Select(s => s.Namespace ?? "").Distinct(StringComparer.Ordinal).Count();
        // Several files in one namespace = one partial type, not competing candidates.
        return namespaces == 1
            ? new GapArtifactCheck
            {
                Kind = GapArtifactKind.Type, Expected = type, Relationship = namesFile ? GapRelationship.Candidate : GapRelationship.Confirmed, Actual = actual, Rule = "declared-type",
                Detail = namesFile ? "Declared, but not in the file the task names (moved or renamed?)." : $"Declared ({found.Count} declaration(s){(found.Count > 1 ? ", one partial type" : "")}); the task names no file.",
            }
            : new GapArtifactCheck { Kind = GapArtifactKind.Type, Expected = type, Relationship = GapRelationship.Ambiguous, Actual = actual, Rule = "declared-type", Detail = $"{type} is declared in {namespaces} namespaces; a person must choose." };
    }

    private static bool IsGenerated(string file)
    {
        var f = Norm(file);
        return f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) || f.Contains("/Generated/", StringComparison.OrdinalIgnoreCase);
    }

    private static GapArtifactCheck PackageCheck(string package, List<string>? projectFiles, SourceIndex index)
    {
        if (!index.Evidence.PackagesCaptured)
            return new GapArtifactCheck { Kind = GapArtifactKind.Package, Expected = package, Relationship = GapRelationship.Unresolved, Rule = "packages-not-captured", Detail = "The snapshot has no declared-dependency inventory." };
        if (!index.Packages.TryGetValue(package, out var found))
            return new GapArtifactCheck { Kind = GapArtifactKind.Package, Expected = package, Relationship = GapRelationship.Missing, Rule = "declared-package", Detail = $"No project in the archive declares {package}." };
        var actual = found.Select(p => new GapSourceRef(Norm(p.OwnerFile), null, $"{p.Name} {p.Version}")).ToList();
        if (projectFiles is { Count: 0 })
            return new GapArtifactCheck
            {
                Kind = GapArtifactKind.Package, Expected = package, Relationship = GapRelationship.Missing, Rule = "declared-package-in-named-project",
                Detail = $"The project the task names is not in the archive. {package} is declared elsewhere ({string.Join(", ", actual.Take(3).Select(a => a.Path))}{(actual.Count > 3 ? ", …" : "")}), which is not evidence for this task.",
            };
        var inProject = projectFiles is not null && found.Any(p => projectFiles.Any(f => Norm(p.OwnerFile).Equals(f, StringComparison.OrdinalIgnoreCase) || p.ReferencedBy.Any(r => Norm(r).Equals(f, StringComparison.OrdinalIgnoreCase))));
        return inProject || projectFiles is null
            ? new GapArtifactCheck { Kind = GapArtifactKind.Package, Expected = package, Relationship = GapRelationship.Confirmed, Actual = actual, Rule = "declared-package", Detail = $"Declared ({string.Join(", ", found.Select(p => p.Version ?? "unversioned").Distinct())}). A declared package is not proof of behaviour." }
            : new GapArtifactCheck { Kind = GapArtifactKind.Package, Expected = package, Relationship = GapRelationship.Candidate, Actual = actual, Rule = "declared-package", Detail = "Declared by another project, not by the project the task names." };
    }

    // ── Verification ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static GapVerificationEvidence Verification(GapTaskInput t, GapTaskAssessment a, GapDocumentSet docs, Dictionary<string, GapTaskAssessment> byKey,
        SourceIndex? index, IReadOnlyList<GapExecutedTest> executions, IReadOnlyList<GapRuntimeObservation> runtime, out List<string> testIds)
    {
        testIds = [];
        if (index is null) return GapVerificationEvidence.NotAssessed;
        // A task whose only expected artifact is a decision record (Draft ADR-…) is not verified by tests.
        if (!t.IsTestTask && a.Artifacts.Count > 0 && a.Artifacts.All(c => c.Rule == "decision-register-entry")) return GapVerificationEvidence.NotApplicable;
        if (runtime.Any(r => r.Subject == t.TaskId || t.DeclaredTypes.Contains(r.Subject))) return GapVerificationEvidence.RuntimeVerified;
        IEnumerable<(GapTaskInput Input, GapTaskAssessment Assessed)> tests;
        if (t.IsTestTask) tests = [(t, a)];
        else
        {
            var refs = docs.Tasks.Where(o => o.IsTestTask && o.DocumentPath == t.DocumentPath && (o.ReferencedTaskIds.Contains(t.TaskId) || t.ReferencedTaskIds.Contains(o.TaskId))).ToList();
            tests = refs.Select(o => (o, byKey[Key(o.DocumentPath, o.TaskId)]));
        }
        var list = tests.ToList();
        testIds = [.. list.Where(x => x.Input.TaskId != t.TaskId).Select(x => x.Input.TaskId)];
        if (list.Count == 0) return GapVerificationEvidence.TestNotFound;
        var states = list.Select(x => TestState(x.Assessed, index, executions)).ToList();
        return states.Max();
    }

    private static GapVerificationEvidence TestState(GapTaskAssessment test, SourceIndex index, IReadOnlyList<GapExecutedTest> executions)
    {
        var files = test.Artifacts.Where(c => c.Kind == GapArtifactKind.Test && c.Relationship is GapRelationship.Confirmed or GapRelationship.Candidate).SelectMany(c => c.Actual.Select(r => r.Path)).ToList();
        if (files.Count == 0) return GapVerificationEvidence.TestNotFound;
        var defs = index.Evidence.Tests.Where(d => files.Contains(Norm(d.FilePath), StringComparer.OrdinalIgnoreCase)).ToList();
        if (defs.Count == 0) return GapVerificationEvidence.TestDefined;
        var runs = executions.Where(e => defs.Any(d => e.FullyQualifiedName.Equals(d.FullyQualifiedName, StringComparison.Ordinal)
            || e.FullyQualifiedName.StartsWith(d.FullyQualifiedName + "(", StringComparison.Ordinal))).ToList();
        if (runs.Count == 0 || runs.All(r => r.Outcome is GapExecutionOutcome.NotExecuted or GapExecutionOutcome.Unknown)) return GapVerificationEvidence.TestDefined;
        var executedDefs = defs.Count(d => runs.Any(r => r.FullyQualifiedName.StartsWith(d.FullyQualifiedName, StringComparison.Ordinal) && r.Outcome is GapExecutionOutcome.Passed or GapExecutionOutcome.Failed));
        return runs.All(r => r.Outcome == GapExecutionOutcome.Passed) && executedDefs == defs.Count ? GapVerificationEvidence.TestPassed : GapVerificationEvidence.TestExecuted;
    }

    // ── Features ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record FeatureScope(string Id, string Name, string Basis, string Doc, List<string> TaskIds, List<GapGateAssessment> Gates);

    private static List<GapFeatureAssessment> Features(GapDocumentSet docs, List<GapGateAssessment> gates, Dictionary<string, GapTaskAssessment> byKey,
        SourceIndex? index, IReadOnlyList<GapRuntimeObservation> runtime)
    {
        var scopes = new List<FeatureScope>();
        // A decision gate delimits a feature (the work the decision governs); other gates are dependencies: they block their tasks in whatever
        // feature those tasks belong to, but do not form a feature of their own.
        List<GapGateAssessment> GatesOf(string doc, IReadOnlyCollection<string> ids) =>
            [.. docs.Gates.Zip(gates).Where(p => p.First.Source.Path == doc && p.Second.BlockedTaskIds.Intersect(ids).Any()).Select(p => p.Second)];
        foreach (var (input, gate) in docs.Gates.Zip(gates).Where(p => p.Second.BlockedTaskIds.Count > 0 && p.First.DecisionIds.Count > 0))
        {
            var title = input.DecisionIds.Select(id => docs.DecisionTitles.GetValueOrDefault(id)).FirstOrDefault(t => t is not null);
            scopes.Add(new FeatureScope($"gate:{input.Source.Path}:{input.GateId}", title ?? input.Label,
                $"Tasks gated by \"{input.Label}\" ({input.Source.Path}:{input.Source.Line})", input.Source.Path, input.BlockedTaskIds, GatesOf(input.Source.Path, input.BlockedTaskIds)));
        }
        foreach (var group in docs.Tasks.Where(t => t.UserStory is not null).GroupBy(t => (t.DocumentPath, t.UserStory!)).OrderBy(g => g.Key.DocumentPath, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal))
        {
            var ids = group.Select(t => t.TaskId).ToList();
            var gs = GatesOf(group.Key.DocumentPath, ids);
            var phase = group.Select(t => t.Phase).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            scopes.Add(new FeatureScope($"story:{group.Key.DocumentPath}:{group.Key.Item2}", $"{group.Key.Item2}{(phase is null ? "" : " — " + phase)}",
                $"Tasks tagged [{group.Key.Item2}] in {group.Key.DocumentPath}", group.Key.DocumentPath, ids, gs));
        }
        return [.. scopes.Select(s => Feature(s, docs, byKey, index, runtime))];
    }

    private static GapFeatureAssessment Feature(FeatureScope s, GapDocumentSet docs, Dictionary<string, GapTaskAssessment> byKey, SourceIndex? index, IReadOnlyList<GapRuntimeObservation> runtime)
    {
        var all = s.TaskIds.Select(id => docs.Tasks.FirstOrDefault(t => t.DocumentPath == s.Doc && t.TaskId == id)).Where(t => t is not null).Select(t => t!).ToList();
        var notApplicable = all.Count(t => t.NotApplicableReason is not null);
        var inputs = all.Where(t => t.NotApplicableReason is null).ToList();
        var assessed = inputs.Select(t => byKey[Key(t.DocumentPath, t.TaskId)]).ToList();
        var implTasks = assessed.Where(a => !a.IsTestTask).ToList();
        var assessable = implTasks.Where(a => a.Implementation != GapImplementationEvidence.NotAssessed).ToList();
        var testTasks = assessed.Where(a => a.IsTestTask).ToList();
        // Tests named by the feature's tasks belong to its verification even when outside its task set.
        var linkedTests = implTasks.SelectMany(a => a.TestTaskIds).Except(testTasks.Select(a => a.TaskId)).Select(id => byKey.GetValueOrDefault(Key(s.Doc, id))).Where(a => a is not null).Select(a => a!).ToList();
        var allTests = testTasks.Concat(linkedTests).ToList();
        var testDefined = allTests.Count(a => a.Verification is >= GapVerificationEvidence.TestDefined);
        var testExecuted = allTests.Count(a => a.Verification is >= GapVerificationEvidence.TestExecuted);
        var testPassed = allTests.Count(a => a.Verification is GapVerificationEvidence.TestPassed or GapVerificationEvidence.RuntimeVerified);
        var confirmed = assessable.Count(a => a.Implementation == GapImplementationEvidence.ConfirmedSourceImplementation);
        var partial = assessable.Count(a => a.Implementation is GapImplementationEvidence.PartialImplementation or GapImplementationEvidence.CandidateImplementation);
        var runtimeVerified = implTasks.Count(a => a.Verification == GapVerificationEvidence.RuntimeVerified);
        var packages = inputs.SelectMany(t => byKey[Key(t.DocumentPath, t.TaskId)].Artifacts.Where(c => c.Kind == GapArtifactKind.Package)).ToList();
        var contracts = inputs.SelectMany(t => t.ContractPaths).Distinct(StringComparer.Ordinal)
            .Select(p => new GapSourceRef(p, null, docs.ContractRequirements.ContainsKey(p) ? "contract document" : "contract document not in the supplied documents")).ToList();
        var requirements = inputs.SelectMany(t => t.RequirementIds).Concat(contracts.SelectMany(c => docs.ContractRequirements.GetValueOrDefault(c.Path) ?? [])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var specified = requirements.Count(r => docs.SpecifiedRequirementIds.Contains(r));
        var unapproved = s.Gates.Where(g => g.Status != GapGateStatus.Approved).ToList();
        var scopeAbsent = implTasks.Count > 0 && implTasks.All(a => a.ScopeAbsentFromArchive || a.Implementation == GapImplementationEvidence.NotAssessed) && implTasks.Any(a => a.ScopeAbsentFromArchive);

        var dimensions = new List<GapDimension>
        {
            new("Requirements completeness", specified, requirements.Count, 0, "Requirement ids named by the feature's tasks and contracts that the supplied specification contains."),
            new("Planning completeness", contracts.Count(c => docs.ContractRequirements.ContainsKey(c.Path)), contracts.Count, 0, "Design contracts named by the feature's tasks that are among the supplied documents."),
            new("Task coverage", assessed.Count(a => a.TaskState == GapTaskState.Completed), assessed.Count, notApplicable, "Tasks marked complete (the checkbox only — not evidence of code); excluded: deferred/optional/out-of-scope tasks."),
            new("Source implementation coverage", confirmed, index is null ? null : assessable.Count, implTasks.Count - assessable.Count, "Implementation tasks whose expected artifacts are all confirmed at the stated location; excluded: tasks naming no source artifact."),
            new("Dependency readiness", packages.Count(c => c.Relationship == GapRelationship.Confirmed), index is null ? null : packages.Count, 0, "Packages the tasks add/pin that a project declares (not proof of behaviour)."),
            new("Architecture decision readiness", s.Gates.Count(g => g.Status == GapGateStatus.Approved), s.Gates.Count, 0, "Mandatory gates of the feature with a recorded approval."),
            new("Test implementation coverage", testDefined, index is null ? null : allTests.Count, 0, "Test tasks (own and referenced) whose test file is in the snapshot."),
            new("Test execution coverage", testExecuted, testDefined == 0 ? null : testDefined, 0, "Defined tests with a recorded execution (imported TRX); none recorded = not assessed."),
            new("Runtime verification coverage", runtimeVerified, runtime.Count == 0 ? null : implTasks.Count, 0, "Implementation tasks with recorded runtime verification."),
        };

        GapReadiness readiness;
        if (index is null) readiness = GapReadiness.NotAssessed;
        else if (unapproved.Count > 0) readiness = GapReadiness.Blocked;
        else if (assessable.Count == 0) readiness = GapReadiness.NotAssessed;
        else if (confirmed == assessable.Count) readiness = allTests.Count > 0 && testPassed == allTests.Count ? GapReadiness.Verified : GapReadiness.ImplementedButNotVerified;
        else if (confirmed + partial > 0) readiness = GapReadiness.PartiallyImplemented;
        else readiness = GapReadiness.NotImplemented;

        var implText = index is null ? "Not assessed (no source snapshot)."
            : scopeAbsent ? $"Implementation not evidenced in supplied archive: {string.Join(", ", implTasks.SelectMany(a => a.DeclaredScopes).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).DefaultIfEmpty("the declared scope"))} has no implementation file in the archive."
            : $"{confirmed} of {assessable.Count} implementation task(s) confirmed, {partial} partial/candidate{(implTasks.Count > assessable.Count ? $", {implTasks.Count - assessable.Count} name no source artifact" : "")}.";
        var testText = index is null ? "Not assessed." : allTests.Count == 0 ? "No test task is associated with the feature's tasks."
            : $"{testDefined} of {allTests.Count} test task(s) have their test file in the snapshot; {testExecuted} executed{(testDefined == 0 ? " (nothing to execute)" : "")}.";
        var runtimeText = runtime.Count == 0 ? "Not assessed — no runtime execution was observed or recorded." : $"{runtimeVerified} of {implTasks.Count} implementation task(s) runtime verified.";
        var depText = index is null ? "Not assessed." : packages.Count == 0 ? "No package is added or pinned by the feature's tasks."
            : string.Join("; ", packages.Select(p => $"{p.Expected}: {p.Relationship} — {p.Detail}"));

        var why = readiness switch
        {
            GapReadiness.Blocked => $"Not ready: {string.Join(" ", unapproved.Select(g => $"gate {g.GateId} is {g.Status} — {g.Reason}"))} {implText}",
            GapReadiness.NotImplemented => $"Not ready: {implText} Implementation is not confirmed — no expected source artifact was found at its stated location. That is not proof the feature is absent.",
            GapReadiness.PartiallyImplemented => $"Not ready: {implText}",
            GapReadiness.ImplementedButNotVerified => $"Implemented but not verified: {testText} {runtimeText}",
            GapReadiness.Verified => $"Verified: every implementation task is confirmed in source and every associated test task has passing recorded executions. {runtimeText}",
            _ => index is null ? "Not assessed: no source snapshot." : "Not assessed: no task of this feature names a source artifact.",
        };
        return new GapFeatureAssessment
        {
            FeatureId = s.Id, Name = s.Name, Basis = s.Basis, DocumentPath = s.Doc, TaskIds = s.TaskIds, ImplementationTaskIds = [.. implTasks.Select(a => a.TaskId)],
            TestTaskIds = [.. allTests.Select(a => a.TaskId).Distinct()], GateIds = [.. s.Gates.Select(g => g.GateId)], Contracts = contracts, RequirementIds = requirements,
            ExpectedArtifacts = [.. implTasks.Concat(allTests).SelectMany(a => a.Artifacts).Select(c => c.Expected).Distinct(StringComparer.Ordinal)],
            Readiness = readiness, Explanation = why.Trim(), Dimensions = dimensions, SourceImplementation = implText, Tests = testText, RuntimeExecution = runtimeText, DependencyEvidence = depText,
        };
    }

    // ── Findings ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<GapFinding> Findings(GapDocumentSet docs, GapSourceEvidence? source, SourceIndex? index, List<GapTaskAssessment> tasks, List<GapGateAssessment> gates,
        List<GapFeatureAssessment> features, DateTimeOffset now)
    {
        var list = new List<GapFinding>();
        GapFinding F(GapFindingType type, string subject, string title, GapFindingSeverity sev, GapConfidence conf, string expected, IEnumerable<GapSourceRef> actual, IEnumerable<string> missing,
            string guidance, IEnumerable<GapSourceRef> sources, string? task = null, string? gate = null, GapFeatureAssessment? feature = null, bool review = false) => new()
        {
            Id = "GAP-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{type}|{subject}"))).ToLowerInvariant()[..12],
            Type = type, Title = title, Severity = sev, Confidence = conf, NeedsHumanReview = review || conf == GapConfidence.Low, Expected = expected,
            ActualEvidence = [.. actual], MissingEvidence = [.. missing], Rule = GapRules.Rules[type], Guidance = guidance, EvaluatedAt = now, Sources = [.. sources],
            TaskId = task, GateId = gate, FeatureId = feature?.FeatureId, FeatureName = feature?.Name, SnapshotId = source?.SnapshotId, ArchiveSha256 = source?.ArchiveSha256,
        };
        GapFeatureAssessment? FeatureOf(GapTaskAssessment t) =>
            features.Where(f => f.DocumentPath == t.Source.Path && f.TaskIds.Contains(t.TaskId)).OrderBy(f => f.FeatureId.StartsWith("gate:", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault();

        // P — inventory limitations.
        if (source is not null)
        {
            var gaps = new List<string>();
            if (!source.FileInventoryComplete) gaps.Add("file inventory incomplete");
            if (!source.SymbolsCaptured) gaps.Add("no declared-type inventory");
            if (source.SymbolsTruncated) gaps.Add("declared-type inventory truncated");
            if (!source.PackagesCaptured) gaps.Add("no declared-package inventory");
            if (!source.TestsCaptured) gaps.Add("no test inventory");
            if (gaps.Count > 0)
                list.Add(F(GapFindingType.InventoryIncomplete, $"snapshot|{string.Join(',', gaps)}", $"Source inventory incomplete: {string.Join(", ", gaps)}", GapFindingSeverity.Info, GapConfidence.High,
                    "A complete inventory of files, declared types, packages and tests", [], gaps, "Checks that depend on the missing inventory are Unresolved, never Missing. Re-analyze the source with the current analyzer.", []));
        }
        foreach (var l in docs.Limitations)
            list.Add(F(GapFindingType.InventoryIncomplete, $"documents|{l}", "Unreadable or skipped input", GapFindingSeverity.Info, GapConfidence.High, "Readable SDD documents", [], [l], "Check the skipped document.", []));

        // Scope limitation — one finding per document and absent scope (never one per task).
        foreach (var group in tasks.Where(t => t.ScopeAbsentFromArchive && t.NotApplicableReason is null).GroupBy(t => t.Source.Path))
        {
            var scopes = group.SelectMany(t => t.DeclaredScopes).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            var ids = group.Select(t => t.TaskId).ToList();
            list.Add(F(GapFindingType.ImplementationScopeNotInArchive, $"{group.Key}|{string.Join(',', scopes)}", $"{string.Join(", ", scopes)} — implementation not evidenced in supplied archive",
                GapFindingSeverity.Info, GapConfidence.High, $"Source under {string.Join(", ", scopes)} for {ids.Count} task(s)", [],
                [$"No file under {string.Join(" or ", scopes)} in {source?.ArchiveName}", $"{ids.Count} task(s) name artifacts there: {Compact(ids)}"],
                "Import the archive that holds the implementation (or the full repository) to assess these tasks. This archive cannot show whether the code exists elsewhere.",
                group.Take(5).Select(t => t.Source)));
        }

        // J/K — gates.
        foreach (var g in gates.Where(g => g.Status != GapGateStatus.Approved))
        {
            var decision = g.GateId.StartsWith("ADR-", StringComparison.Ordinal);
            if (decision)
                list.Add(F(GapFindingType.DecisionNotApproved, $"{g.Evidence[0].Path}|{g.GateId}", $"{g.GateId} — no recorded approval ({g.Status})", GapFindingSeverity.High, GapConfidence.High,
                    $"{g.GateId} recorded as approved in the decision register", g.Evidence.Skip(1), [g.Reason], $"Record the decision and its sign-off ({g.Label}) in the decision register, then re-evaluate. BirkNext never approves a decision.",
                    g.Evidence, gate: g.GateId));
            if (g.BlockedTaskIds.Count > 0)
                list.Add(F(GapFindingType.BlockedByGate, $"{g.Evidence[0].Path}|{g.GateId}|blocked", $"{Compact(g.BlockedTaskIds)} blocked by {g.GateId} ({g.Status})", GapFindingSeverity.High, GapConfidence.High,
                    $"{g.GateId} approved before {Compact(g.BlockedTaskIds)}", [g.Evidence[0]], [$"Gate status: {g.Status}"], "Dependent tasks cannot be claimed ready until the gate is approved.", g.Evidence, gate: g.GateId,
                    feature: features.FirstOrDefault(f => f.GateIds.Contains(g.GateId) && f.FeatureId.StartsWith("gate:", StringComparison.Ordinal))));
        }

        // Per task (only where the declared scope is present: otherwise the scope finding covers them).
        if (index is not null)
            foreach (var t in tasks.Where(t => !t.ScopeAbsentFromArchive && t.NotApplicableReason is null))
            {
                var feature = FeatureOf(t);
                var sources = new[] { t.Source };
                if (t.TaskState == GapTaskState.Completed && t.Implementation == GapImplementationEvidence.NoMatchingSource)
                    list.Add(F(GapFindingType.CompletedTaskWithoutImplementation, $"{t.Source.Path}|{t.TaskId}", $"{t.TaskId} is checked complete, but no expected artifact exists", GapFindingSeverity.High, GapConfidence.High,
                        string.Join(", ", t.Artifacts.Select(a => a.Expected)), [], t.Artifacts.Where(a => a.Relationship == GapRelationship.Missing).Select(a => $"{a.Expected}: {a.Detail}"),
                        "Either the checkbox is wrong or the code lives outside this archive: open the task and the source inventory.", sources, t.TaskId, feature: feature));
                if (t.TaskState == GapTaskState.NotStarted && t.Implementation is GapImplementationEvidence.ConfirmedSourceImplementation or GapImplementationEvidence.PartialImplementation)
                    list.Add(F(GapFindingType.OpenTaskWithImplementation, $"{t.Source.Path}|{t.TaskId}", $"{t.TaskId} is unchecked, but its artifacts exist", GapFindingSeverity.Low, GapConfidence.Medium,
                        "Checkbox matching the source", t.Artifacts.Where(a => a.Relationship == GapRelationship.Confirmed).SelectMany(a => a.Actual), ["Task checkbox not updated?"],
                        "Review: the task may be done (update tasks.md) or the artifact may predate the task.", sources, t.TaskId, feature: feature, review: true));
                foreach (var c in t.Artifacts)
                {
                    var subject = $"{t.Source.Path}|{t.TaskId}|{c.Kind}|{c.Expected}";
                    switch (c.Relationship, c.Kind)
                    {
                        case (GapRelationship.Missing, GapArtifactKind.File) when c.Rule != "decision-register-entry":
                            list.Add(F(GapFindingType.TaskFileMissing, subject, $"{t.TaskId}: {System.IO.Path.GetFileName(c.Expected)} not found", t.TaskState == GapTaskState.Completed ? GapFindingSeverity.High : GapFindingSeverity.Medium,
                                GapConfidence.High, c.Expected, [], [c.Detail], "Check whether the file was renamed with a different name, or the task is not done.", sources, t.TaskId, feature: feature));
                            break;
                        case (GapRelationship.Missing, GapArtifactKind.Type):
                            list.Add(F(GapFindingType.TaskTypeMissing, subject, $"{t.TaskId}: type {c.Expected} not declared", t.TaskState == GapTaskState.Completed ? GapFindingSeverity.High : GapFindingSeverity.Medium,
                                GapConfidence.High, c.Expected, [], [c.Detail], "Search for a renamed type; if none, the implementation is missing.", sources, t.TaskId, feature: feature));
                            break;
                        case (GapRelationship.Missing, GapArtifactKind.Package):
                            list.Add(F(GapFindingType.PackageMissing, subject, $"{t.TaskId}: package {c.Expected} not declared", GapFindingSeverity.Medium, GapConfidence.High, c.Expected, [], [c.Detail],
                                "Add the package (after any gate it depends on) or record why it is not needed.", sources, t.TaskId, feature: feature));
                            break;
                        case (GapRelationship.Missing, GapArtifactKind.Test):
                            list.Add(F(GapFindingType.TestMissing, subject, $"{t.TaskId}: test {System.IO.Path.GetFileName(c.Expected)} not found", GapFindingSeverity.Medium, GapConfidence.High, c.Expected, [], [c.Detail],
                                "Write the test, or link the test that covers this behaviour.", sources, t.TaskId, feature: feature));
                            break;
                        case (GapRelationship.Candidate, GapArtifactKind.File or GapArtifactKind.Test or GapArtifactKind.Type) when c.Rule is "same-file-name" or "declared-type" or "renamed-type-candidate" or "generated-source":
                            list.Add(F(GapFindingType.ArtifactMovedOrRenamed, subject, $"{t.TaskId}: {c.Expected} found elsewhere", GapFindingSeverity.Low, GapConfidence.Low, c.Expected, c.Actual, [c.Detail],
                                "Confirm the found file/type is the one the task means (then it is implemented), or reject it.", sources, t.TaskId, feature: feature, review: true));
                            break;
                        case (GapRelationship.Ambiguous, _):
                            list.Add(F(GapFindingType.MultipleCandidates, subject, $"{t.TaskId}: several candidates for {c.Expected}", GapFindingSeverity.Low, GapConfidence.Low, c.Expected, c.Actual, [c.Detail],
                                "Choose the implementation that belongs to this task.", sources, t.TaskId, feature: feature, review: true));
                            break;
                    }
                }
                // F — the package exists, but the feature's types do not.
                if (t.Artifacts.Any(c => c.Kind == GapArtifactKind.Package && c.Relationship is GapRelationship.Confirmed or GapRelationship.Candidate) && feature is not null
                    && tasks.Where(o => feature.ImplementationTaskIds.Contains(o.TaskId) && o.Source.Path == t.Source.Path && o.TaskId != t.TaskId).Any(o => o.Artifacts.Any(c => c.Kind == GapArtifactKind.Type && c.Relationship == GapRelationship.Missing)))
                    list.Add(F(GapFindingType.PackageWithoutBehaviour, $"{t.Source.Path}|{t.TaskId}|behaviour", $"{t.TaskId}: package declared, behaviour not implemented", GapFindingSeverity.Medium, GapConfidence.High,
                        "Types of the feature that use the package", t.Artifacts.Where(c => c.Kind == GapArtifactKind.Package).SelectMany(c => c.Actual),
                        tasks.Where(o => feature.ImplementationTaskIds.Contains(o.TaskId)).SelectMany(o => o.Artifacts).Where(c => c.Kind == GapArtifactKind.Type && c.Relationship == GapRelationship.Missing).Select(c => $"{c.Expected} not declared"),
                        "A declared dependency is not behaviour: implement the types that use it.", sources, t.TaskId, feature: feature));
            }

        // L/H/M — features.
        foreach (var f in features)
        {
            var sources = f.Contracts.Concat([new GapSourceRef(f.DocumentPath)]).ToList();
            var noImpl = f.ImplementationTaskIds.Count > 0 && f.Readiness is GapReadiness.NotImplemented || (f.Readiness == GapReadiness.Blocked && f.Dimensions.Single(d => d.Name == "Source implementation coverage") is { Numerator: 0, Denominator: > 0 });
            if (noImpl && (f.Contracts.Count > 0 || f.FeatureId.StartsWith("gate:", StringComparison.Ordinal)))
                list.Add(F(GapFindingType.IntegrationNotImplemented, $"{f.FeatureId}|not-implemented",
                    $"{f.Name} — implementation not evidenced{(f.SourceImplementation.StartsWith("Implementation not evidenced in supplied archive", StringComparison.Ordinal) ? " in supplied archive" : "")}",
                    f.FeatureId.StartsWith("gate:", StringComparison.Ordinal) ? GapFindingSeverity.High : GapFindingSeverity.Medium, GapConfidence.High,
                    $"Source for {Compact(f.ImplementationTaskIds)} per {string.Join(", ", f.Contracts.Select(c => c.Path).DefaultIfEmpty("the tasks"))}",
                    [], [f.SourceImplementation, f.Tests, f.RuntimeExecution, .. f.GateIds.Select(g => $"Gate {g}: {gates.First(x => x.GateId == g).Status}")],
                    f.Readiness == GapReadiness.Blocked ? "Resolve the gate first; then implement the tasks and re-evaluate." : "Implement the tasks (or import the archive that holds them) and re-evaluate.",
                    sources, feature: f));
            var defined = f.Dimensions.Single(d => d.Name == "Test implementation coverage").Numerator;
            var executed = f.Dimensions.Single(d => d.Name == "Test execution coverage").Numerator;
            if (defined > executed)
                list.Add(F(GapFindingType.TestNotExecuted, $"{f.FeatureId}|not-executed", $"{f.Name}: {defined - executed} defined test task(s) without recorded execution", GapFindingSeverity.Medium, GapConfidence.High,
                    "Recorded executions (TRX) for the feature's tests", [], [f.Tests], "Import the TRX of a run that executed these tests; a test definition is not an execution.", sources, feature: f));
            var implemented = f.Dimensions.Single(d => d.Name == "Source implementation coverage").Numerator;
            if (implemented > 0 && f.Dimensions.Single(d => d.Name == "Runtime verification coverage") is { Numerator: 0 })
                list.Add(F(GapFindingType.NoRuntimeVerification, $"{f.FeatureId}|no-runtime", $"{f.Name}: static evidence only, no runtime verification", GapFindingSeverity.Low, GapConfidence.High,
                    "Observed runtime behaviour", [], [f.RuntimeExecution], "Run the feature in a test environment and record the observation.", sources, feature: f));
        }

        // I — production projects in a module with tasks that no task names.
        if (index is not null)
            foreach (var module in docs.Tasks.Select(t => ImplementationGapExtractor.ModuleRoot(t.DocumentPath)).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal))
            {
                var text = string.Join("\n", docs.Tasks.Where(t => t.DocumentPath.StartsWith(module, StringComparison.Ordinal)).Select(t => t.Text + "\n" + string.Join("\n", t.Continuation)));
                foreach (var p in index.Evidence.Projects.Where(p => !p.IsTest && Norm(p.Path).StartsWith(module, StringComparison.OrdinalIgnoreCase) && !text.Contains(p.Name, StringComparison.Ordinal)))
                    list.Add(F(GapFindingType.ImplementationWithoutTask, $"{module}|{p.Name}", $"{p.Name}: no task names this project", GapFindingSeverity.Low, GapConfidence.Low, $"A task naming {p.Name}",
                        [new GapSourceRef(Norm(p.Path))], ["No explicit task association"], "Link the project to its tasks, or confirm it is outside the specified scope.", [new GapSourceRef(Norm(p.Path))], review: true));
            }

        return [.. list.GroupBy(f => f.Id).Select(g => g.First()).OrderBy(f => f.Severity).ThenBy(f => f.Type).ThenBy(f => f.TaskId, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal)];
    }

    /// <summary>T119, T120, T121, T122 → "T119–T122"; non-consecutive ids stay listed.</summary>
    public static string Compact(IEnumerable<string> ids)
    {
        var nums = ids.Select(i => (Id: i, N: int.TryParse(i.TrimStart('T'), out var n) ? n : -1)).OrderBy(x => x.N).ToList();
        var parts = new List<string>();
        for (var i = 0; i < nums.Count;)
        {
            var j = i;
            while (j + 1 < nums.Count && nums[j].N >= 0 && nums[j + 1].N == nums[j].N + 1) j++;
            parts.Add(j - i >= 2 ? $"{nums[i].Id}–{nums[j].Id}" : string.Join(", ", nums.Skip(i).Take(j - i + 1).Select(x => x.Id)));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
