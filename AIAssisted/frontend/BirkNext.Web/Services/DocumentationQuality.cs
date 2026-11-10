using System.Security.Cryptography;
using System.Text;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Evidence state of one module · documentation area. Missing evidence is never drift; a candidate is never a completed comparison.</summary>
public enum QualityAreaStatus { Compared, NotAssessed, MissingGenerated, MissingAuthored, Ambiguous, NotSupported }

/// <summary>Analyzer verdict of one finding (the human review is kept apart, see <see cref="QualityFinding.ReviewState"/>).</summary>
public enum FindingClassification { Consistent, PotentialDrift, Conflict, NeedsReview, NotComparable }

public enum FindingSeverity { High, Medium, Low, NeedsReview, None }

/// <summary>Human review decisions (append-only).</summary>
public enum DriftReviewDecision { ConfirmDifference, AcceptExpectedChange, FalsePositive, RequestClarification, Defer, ResolvedWithEvidence }

/// <summary>Which eligible areas a bulk run compares.</summary>
public enum BulkScope { AllEligible, Selected, Changed }

/// <summary>One module · documentation area with its evidence state and, when compared with the current inputs, that comparison.</summary>
public sealed record QualityArea(string Key, string ImportId, string Module, DriftConcern Concern, QualityAreaStatus Status, string Reason, int AuthoredDocuments,
    int GeneratedDocuments, SddDocumentationDriftComparison? Comparison, bool HasEarlierComparison, DriftCandidateGroup? Group);

/// <summary>One finding: stable id (lineage · module · area · concept · category), expected vs actual, evidence, rule, severity with rationale, and the review.</summary>
public sealed record QualityFinding(
    string Id, string ImportId, string Module, string Concern, string Concept, string Category, FindingClassification Classification, FindingSeverity Severity,
    string SeverityRationale, string? Expected, string? Actual, IReadOnlyList<string> AuthoredReferences, string? GeneratedReference, IReadOnlyList<string> SpecificationPacks,
    string Rule, string Confidence, string Explanation, string InputsFingerprint, Guid ComparisonId, DateTimeOffset ComparedAt, SddDriftReview? Review,
    IReadOnlyList<SddDriftReview> History)
{
    /// <summary>Unreviewed, a decision, or Reopened when the latest decision was made on other inputs (a newer comparison).</summary>
    public string ReviewState => Review is null ? (History.Count > 0 ? "Reopened" : "Unreviewed") : Review.Decision;
    /// <summary>A decision that closes the question for these inputs.</summary>
    public bool Closed => Review?.Decision is nameof(DriftReviewDecision.ConfirmDifference) or nameof(DriftReviewDecision.AcceptExpectedChange)
        or nameof(DriftReviewDecision.FalsePositive) or nameof(DriftReviewDecision.ResolvedWithEvidence);
    public bool IsDifference => Classification is FindingClassification.PotentialDrift or FindingClassification.Conflict or FindingClassification.NeedsReview;
}

public sealed record QualitySummary(int AuthoredDocuments, int GeneratedDocuments, int Areas, int Comparable, int Compared, int NotAssessed, int MissingGenerated,
    int MissingAuthored, int Ambiguous, int NotSupported, int ConfirmedDifferences, int PotentialDrift, int NeedsReview, int Conflicts, int ConsistentConcepts,
    int ReviewedFindings, int StaleAreas);

public sealed record BulkComparisonRow(string Key, string Module, string Concern, string Outcome, string Detail);

/// <summary>An exact-content relocation of a document between two imports of the same project lineage.</summary>
public sealed record DocumentMove(string FromPath, string ToPath, DriftOrigin Origin, string Fingerprint, string FromImport, string ToImport);

/// <summary>
/// Documentation Quality: areas, bulk comparison, findings, review and report on top of the one Documentation Drift engine
/// (<see cref="DocumentationDriftComposite.BuildDataModel"/> / <see cref="DocumentationDriftComposite.CompareDataModel"/> / Save). No second
/// comparator, no second store: comparisons and reviews live in the workspace lifecycle. Only the Data Model has a structured extractor; other
/// generated areas are reported NOT SUPPORTED, never compared by text similarity.
/// </summary>
public static class DocumentationQuality
{
    public const string RuleSet = "DataModel structural v" + "1";
    public const string NotAssessedText = "Not assessed";

    public static string InputsFingerprint(IEnumerable<string> contributorFingerprints, string generatedFingerprint) =>
        Sha(string.Join('|', contributorFingerprints.OrderBy(f => f, StringComparer.Ordinal)) + "|" + generatedFingerprint + "|v" + DocumentationDriftComposite.AlgorithmVersion)[..16];

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>Every module · area of the import: structured candidates (Data Model) with their evidence state, plus generated areas without a
    /// structured extractor (NOT SUPPORTED, never drift).</summary>
    public static IReadOnlyList<QualityArea> Areas(IReadOnlyList<DriftDocument> catalog, IReadOnlyList<SddDocumentationDriftComparison> saved, string? importId, IReadOnlyCollection<string>? projectImports = null)
    {
        if (importId is null) return [];
        var areas = new List<QualityArea>();
        foreach (var g in DocumentationDriftComposite.CandidateGroups(catalog, saved, importId, projectImports))
        {
            var current = g.Generated is { } gen && g.State == DriftGroupState.ComparisonAvailable ? DocumentationDriftComposite.SavedFor(saved, g.Contributors, gen) : null;
            var earlier = saved.Any(s => s.ComparisonKind == DocumentationDriftComposite.StructuredDataModel && string.Equals(s.Module, g.Module, StringComparison.OrdinalIgnoreCase)
                && s.Concern == g.Concern.ToString() && s != current);
            var (status, reason) = g.State switch
            {
                DriftGroupState.ComparisonAvailable when current is not null => (QualityAreaStatus.Compared, "Compared with the current authored and generated documents."),
                DriftGroupState.ComparisonAvailable => (QualityAreaStatus.NotAssessed, earlier ? "Inputs changed since the last comparison: compare again." : "Comparable; not compared yet."),
                DriftGroupState.GeneratedNotPresent => (QualityAreaStatus.MissingGenerated, "Authored documentation exists; no generated documentation for this module and area. Missing evidence, not drift."),
                DriftGroupState.AuthoredNotPresent => (QualityAreaStatus.MissingAuthored, "Generated documentation exists; no authored documentation for this module and area. Missing evidence, not drift."),
                DriftGroupState.MultipleGeneratedCandidates => (QualityAreaStatus.Ambiguous, $"{g.GeneratedCandidates.Count} generated candidates: a person chooses; never compared automatically."),
                _ => (QualityAreaStatus.NotSupported, "The generated document's content was not captured, so it cannot be compared structurally."),
            };
            areas.Add(new(g.Key, importId, g.Module, g.Concern, status, reason, g.Contributors.Count, g.GeneratedCandidates.Count, current, earlier, g));
        }
        // Generated areas without a structured extractor (Architecture, Messaging, Overview, …): listed, never compared.
        var structuredKeys = areas.Select(a => (a.Module.ToLowerInvariant(), a.Concern)).ToHashSet();
        foreach (var grp in catalog.Where(d => d.Origin == DriftOrigin.Generated && (d.ProjectImportId == importId || (d.ProjectImportId is { } o && projectImports?.Contains(o) == true))
                     && d.Module is { } m && m != "(repository)" && !DocumentationDriftComposite.Structured(DocumentationDriftComposite.ConcernOf(d)))
                 .GroupBy(d => (Module: d.Module!, Concern: DocumentationDriftComposite.ConcernOf(d))))
        {
            if (grp.Key.Concern is DriftConcern.Other || structuredKeys.Contains((grp.Key.Module.ToLowerInvariant(), grp.Key.Concern))) continue;
            areas.Add(new($"{importId}|{grp.Key.Module.ToLowerInvariant()}|{grp.Key.Concern}|unsupported", importId, grp.Key.Module, grp.Key.Concern, QualityAreaStatus.NotSupported,
                $"No structured extractor for {DocumentationDriftComposite.ConcernLabel(grp.Key.Concern)}: not compared (never by text similarity).", 0, grp.Count(), null, false, null));
        }
        return [.. areas.OrderBy(a => a.Module, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Concern)];
    }

    /// <summary>
    /// Compares the eligible areas in one action through the one engine. Idempotent: an area already compared with exactly the current inputs is
    /// "Unchanged" and nothing is saved again; ambiguous, missing and unsupported areas are never compared; a missing captured text is reported.
    /// </summary>
    public static IReadOnlyList<BulkComparisonRow> RunBulk(SddLifecycleState state, IReadOnlyList<QualityArea> areas, BulkScope scope, IReadOnlySet<string>? selected,
        IDataModelAnalysisService parser, string? sessionScope, DateTimeOffset now)
    {
        var rows = new List<BulkComparisonRow>();
        foreach (var a in areas)
        {
            if (scope == BulkScope.Selected && selected?.Contains(a.Key) != true) continue;
            var concern = DocumentationDriftComposite.ConcernLabel(a.Concern);
            if (a.Status == QualityAreaStatus.Compared) { rows.Add(new(a.Key, a.Module, concern, "Unchanged", "Already compared with exactly these inputs; nothing saved again.")); continue; }
            if (a.Status != QualityAreaStatus.NotAssessed || a.Group is not { Generated: { } generated } g) { rows.Add(new(a.Key, a.Module, concern, "Skipped", a.Reason)); continue; }
            if (scope == BulkScope.Changed && !a.HasEarlierComparison) continue;
            var texts = g.Contributors.Select(c => (c, DocumentationDrift.Text(state, c))).Where(x => x.Item2 is not null).Select(x => (x.c, x.Item2!)).ToList();
            if (texts.Count != g.Contributors.Count || DocumentationDrift.Text(state, generated) is not { } genText)
            { rows.Add(new(a.Key, a.Module, concern, "Skipped", "A document's text was not captured at import; it cannot be compared.")); continue; }
            try
            {
                var intent = DocumentationDriftComposite.BuildDataModel(g.ImportId, g.Module, texts, parser);
                var result = DocumentationDriftComposite.CompareDataModel(intent, generated, genText, parser);
                DocumentationDriftComposite.Save(state, result, sessionScope, now);
                rows.Add(new(a.Key, a.Module, concern, "Compared",
                    $"{result.Discrepancies} difference(s), {result.Count(ConsistencyCategory.AuthoredOnly)} authored-only, {result.Count(ConsistencyCategory.GeneratedOnly)} generated-only, {intent.Conflicts.Count} authored conflict(s)"));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                rows.Add(new(a.Key, a.Module, concern, "Failed", "The documents could not be parsed for a structured comparison."));
            }
        }
        return rows;
    }

    private static (FindingClassification, FindingSeverity, string) Classify(string category, string? expected) => category switch
    {
        nameof(ConsistencyCategory.FieldDifference) => (FindingClassification.PotentialDrift, FindingSeverity.Medium, "A field's type differs between authored intent and implementation-derived documentation."),
        nameof(ConsistencyCategory.ConstraintDifference) => (FindingClassification.PotentialDrift, FindingSeverity.Medium, "A field's required/optional constraint differs."),
        nameof(ConsistencyCategory.RelationshipDifference) => (FindingClassification.PotentialDrift, FindingSeverity.Medium, "An authored relationship is not represented in the implementation-derived documentation."),
        nameof(ConsistencyCategory.AuthoredOnly) => (FindingClassification.PotentialDrift, FindingSeverity.Medium,
            $"An authored {(expected ?? "concept").ToLowerInvariant()} is not represented in the implementation-derived documentation (missing implementation concept or outdated specification)."),
        nameof(ConsistencyCategory.GeneratedOnly) => (FindingClassification.NeedsReview, FindingSeverity.Low, "Additional implementation detail not stated in authored intent; not automatically a defect."),
        nameof(ConsistencyCategory.NotConcludedAuthoredConflict) => (FindingClassification.Conflict, FindingSeverity.NeedsReview, "The authored documents disagree; which statement applies must be decided by a person."),
        nameof(ConsistencyCategory.NotComparable) => (FindingClassification.NotComparable, FindingSeverity.None, "The generated documentation does not represent this; not compared."),
        _ => (FindingClassification.Consistent, FindingSeverity.None, "Consistent."),
    };

    /// <summary>The findings of every compared area (its current comparison), with stable ids, evidence and the review history.</summary>
    public static IReadOnlyList<QualityFinding> Findings(SddLifecycleState state, IReadOnlyList<QualityArea> areas)
    {
        var findings = new List<QualityFinding>();
        foreach (var a in areas.Where(a => a.Comparison is not null))
        {
            var c = a.Comparison!;
            var inputs = InputsFingerprint(c.Contributors.Select(x => x.Fingerprint), c.Right.Fingerprint);
            var lineage = c.Right.LineageKey ?? c.Contributors.FirstOrDefault()?.LineageKey ?? a.ImportId;
            foreach (var item in c.Items)
            {
                var refs = item.Left.Where(x => x.Contains(" · ", StringComparison.Ordinal)).ToList();
                var expected = item.Left.FirstOrDefault(x => !x.Contains(" · ", StringComparison.Ordinal));
                var generatedRef = item.Right.LastOrDefault();
                var actual = item.Right.Count > 1 ? item.Right[0] : null;
                var (classification, severity, rationale) = Classify(item.Kind, expected);
                var id = "dq-" + Sha($"{lineage}|{a.Module.ToLowerInvariant()}|{a.Concern}|{item.Section.ToLowerInvariant()}|{item.Kind}")[..12];
                var history = state.DocumentationDriftReviews.Where(r => r.FindingId == id).OrderBy(r => r.At).ToList();
                var current = history.LastOrDefault(r => r.InputsFingerprint == inputs);
                var packs = refs.Select(r => Explorers.ArtifactModules.FeatureOf(r.Split(" · ")[0])).Where(p => p is not null).Select(p => p!).Distinct().ToList();
                findings.Add(new(id, a.ImportId, a.Module, DocumentationDriftComposite.ConcernLabel(a.Concern), item.Section, item.Kind, classification, severity, rationale,
                    expected, actual, refs, generatedRef, packs, $"{RuleSet} · {item.Kind}", classification == FindingClassification.Consistent ? "Exact structural match" : "Structural (extracted concepts)",
                    item.Detail, inputs, c.Id, c.CreatedAt, current, history));
            }
        }
        return findings;
    }

    /// <summary>Records a review decision (append-only) against the finding's current inputs.</summary>
    public static SddDriftReview Review(SddLifecycleState state, QualityFinding finding, DriftReviewDecision decision, string? note, string reviewer, DateTimeOffset now)
    {
        var review = new SddDriftReview
        {
            FindingId = finding.Id, Decision = decision.ToString(), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 1000)],
            Reviewer = string.IsNullOrWhiteSpace(reviewer) ? "local user" : reviewer.Trim()[..Math.Min(reviewer.Trim().Length, 200)], At = now,
            InputsFingerprint = finding.InputsFingerprint, ComparisonId = finding.ComparisonId, Module = finding.Module, Concern = finding.Concern, Concept = finding.Concept,
        };
        state.DocumentationDriftReviews.Add(review);
        return review;
    }

    public static QualitySummary Summary(IReadOnlyList<DriftDocument> catalog, string? importId, IReadOnlyList<QualityArea> areas, IReadOnlyList<QualityFinding> findings)
    {
        var docs = catalog.Where(d => d.ProjectImportId == importId).ToList();
        int A(QualityAreaStatus s) => areas.Count(a => a.Status == s);
        var open = findings.Where(f => f.IsDifference).ToList();
        return new(docs.Count(d => d.Origin == DriftOrigin.Authored), docs.Count(d => d.Origin == DriftOrigin.Generated), areas.Count,
            A(QualityAreaStatus.Compared) + A(QualityAreaStatus.NotAssessed), A(QualityAreaStatus.Compared), A(QualityAreaStatus.NotAssessed),
            A(QualityAreaStatus.MissingGenerated), A(QualityAreaStatus.MissingAuthored), A(QualityAreaStatus.Ambiguous), A(QualityAreaStatus.NotSupported),
            open.Count(f => f.Review?.Decision == nameof(DriftReviewDecision.ConfirmDifference)),
            open.Count(f => f.Classification == FindingClassification.PotentialDrift && !f.Closed),
            open.Count(f => (f.Classification is FindingClassification.NeedsReview or FindingClassification.Conflict && !f.Closed)
                || f.Review?.Decision is nameof(DriftReviewDecision.RequestClarification) or nameof(DriftReviewDecision.Defer)),
            open.Count(f => f.Classification == FindingClassification.Conflict),
            findings.Count(f => f.Classification == FindingClassification.Consistent), findings.Count(f => f.Review is not null),
            areas.Count(a => a.Status == QualityAreaStatus.NotAssessed && a.HasEarlierComparison));
    }

    /// <summary>Exact-content moves between consecutive imports of one lineage: same fingerprint, new path, old path gone. Changed content at a
    /// new path is not claimed as a move (it shows as removed + added).</summary>
    public static IReadOnlyList<DocumentMove> Moves(IReadOnlyList<DriftDocument> catalog, IReadOnlyList<DriftImport> imports, string lineage)
    {
        var ordered = imports.Where(i => i.LineageKey == lineage).OrderBy(i => i.Number).ToList();
        var moves = new List<DocumentMove>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var before = catalog.Where(d => d.ProjectImportId == ordered[i - 1].ImportId).ToList();
            var after = catalog.Where(d => d.ProjectImportId == ordered[i].ImportId).ToList();
            var beforePaths = before.Select(DocumentationDrift.StablePathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var afterPaths = after.Select(DocumentationDrift.StablePathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in after.Where(d => !beforePaths.Contains(DocumentationDrift.StablePathOf(d))))
                if (before.Where(b => b.Origin == d.Origin && b.Fingerprint == d.Fingerprint && !afterPaths.Contains(DocumentationDrift.StablePathOf(b))).ToList() is [var only])
                    moves.Add(new(DocumentationDrift.StablePathOf(only), DocumentationDrift.StablePathOf(d), d.Origin, d.Fingerprint, ordered[i - 1].Label, ordered[i].Label));
        }
        return moves;
    }

    /// <summary>A Markdown quality report of the selected import. "Not assessed" — never "0 drift" — when nothing was compared.</summary>
    public static string Report(DriftImport? import, IReadOnlyList<QualityArea> areas, IReadOnlyList<QualityFinding> findings, QualitySummary s, IReadOnlyList<DocumentMove> moves, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        string Md(string? v) => (v ?? "").Replace("|", "\\|").Replace("\n", " ");
        sb.AppendLine("# Documentation quality report").AppendLine();
        sb.AppendLine($"Generated {now:yyyy-MM-dd HH:mm} UTC by BirkNext Documentation Quality (rule set {RuleSet}). Evidence-based; local analysis only.").AppendLine();
        sb.AppendLine("## 1. Executive summary").AppendLine();
        if (s.Compared == 0) sb.AppendLine($"- Drift: **{NotAssessedText}** — no comparison has been performed for this import.");
        else sb.AppendLine($"- {s.Compared} of {s.Comparable} comparable areas compared; {s.ConfirmedDifferences} confirmed difference(s), {s.PotentialDrift} potential drift, {s.NeedsReview} need review, {s.Conflicts} authored conflict(s).");
        sb.AppendLine($"- Missing evidence: {s.MissingGenerated} area(s) without generated documentation, {s.MissingAuthored} without authored documentation (not drift).");
        sb.AppendLine($"- Not supported: {s.NotSupported} area(s) without a structured extractor; ambiguous pairings: {s.Ambiguous}.").AppendLine();
        sb.AppendLine("## 2. Project and snapshot").AppendLine();
        sb.AppendLine(import is null ? "- No import selected." : $"- {Md(import.Label)} · archive {Md(import.ArchiveName)} · imported {import.ImportedAt:yyyy-MM-dd HH:mm} UTC · source snapshot {import.SourceSnapshotId?.ToString() ?? "not recorded"}").AppendLine();
        sb.AppendLine("## 3. Evidence completeness").AppendLine();
        sb.AppendLine($"- Authored documents: {s.AuthoredDocuments} · generated documents: {s.GeneratedDocuments}");
        foreach (var a in areas.Where(a => a.Status is QualityAreaStatus.MissingGenerated or QualityAreaStatus.MissingAuthored or QualityAreaStatus.Ambiguous))
            sb.AppendLine($"- {Md(a.Module)} · {DocumentationDriftComposite.ConcernLabel(a.Concern)}: {a.Status} — {Md(a.Reason)}");
        sb.AppendLine().AppendLine("## 4. Comparison coverage").AppendLine();
        sb.AppendLine("| Module | Area | Status | Authored | Generated |").AppendLine("|---|---|---|---|---|");
        foreach (var a in areas) sb.AppendLine($"| {Md(a.Module)} | {DocumentationDriftComposite.ConcernLabel(a.Concern)} | {(a.Status == QualityAreaStatus.NotAssessed ? NotAssessedText : a.Status.ToString())} | {a.AuthoredDocuments} | {a.GeneratedDocuments} |");
        void Section(string title, IEnumerable<QualityFinding> list)
        {
            var l = list.ToList();
            sb.AppendLine().AppendLine(title).AppendLine();
            if (l.Count == 0) { sb.AppendLine(s.Compared == 0 ? $"- {NotAssessedText}." : "- None."); return; }
            sb.AppendLine("| Finding | Module | Concept | Expected | Actual | Severity | Review |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var f in l) sb.AppendLine($"| {f.Id} | {Md(f.Module)} | {Md(f.Concept)} | {Md(f.Expected)} | {Md(f.Actual)} | {f.Severity} | {f.ReviewState} |");
        }
        Section("## 5. Confirmed differences", findings.Where(f => f.Review?.Decision == nameof(DriftReviewDecision.ConfirmDifference)));
        Section("## 6. Potential drift", findings.Where(f => f.Classification == FindingClassification.PotentialDrift && !f.Closed));
        sb.AppendLine().AppendLine("## 7. Missing evidence").AppendLine();
        sb.AppendLine($"- {s.MissingGenerated} area(s) lack generated documentation; {s.MissingAuthored} lack authored documentation. Counted as missing evidence, never as drift.");
        sb.AppendLine().AppendLine("## 8. Requirements impact").AppendLine();
        var packs = findings.Where(f => f.IsDifference).SelectMany(f => f.SpecificationPacks.Select(p => (f.Module, Pack: p))).Distinct().ToList();
        sb.AppendLine(packs.Count == 0 ? "- No specification pack is linked to an open finding by provenance." : string.Join(Environment.NewLine, packs.Select(p => $"- {Md(p.Module)}: specification pack `{Md(p.Pack)}` contributed to an open finding (provenance, not a confirmed requirement defect).")));
        sb.AppendLine().AppendLine("## 9. Test impact").AppendLine();
        sb.AppendLine("- Not assessed: findings are not linked to test definitions by evidence in this version.");
        sb.AppendLine().AppendLine("## 10. Human review status").AppendLine();
        sb.AppendLine($"- {s.ReviewedFindings} finding(s) reviewed; {findings.Count(f => f.IsDifference && f.Review is null)} open finding(s) unreviewed; {findings.Count(f => f.ReviewState == "Reopened")} reopened by newer inputs.");
        sb.AppendLine().AppendLine("## 11. Version changes").AppendLine();
        sb.AppendLine(moves.Count == 0 ? "- No exact-content document moves detected (or only one import in this lineage)." : string.Join(Environment.NewLine, moves.Select(m => $"- Moved ({m.Origin}): `{Md(m.FromPath)}` → `{Md(m.ToPath)}` ({Md(m.FromImport)} → {Md(m.ToImport)})")));
        sb.AppendLine().AppendLine("## 12. Remaining limitations").AppendLine();
        sb.AppendLine("- Structured comparison exists for Data Model documentation only; other areas are listed as not supported, never compared by text similarity.");
        sb.AppendLine("- Semantic equivalence of reworded text is not claimed; descriptions are not compared.");
        sb.AppendLine("- Applicability of several authored documents is decided only by explicit change statements; other disagreements are authored conflicts for review.");
        sb.AppendLine().AppendLine("## 13. Evidence references").AppendLine();
        foreach (var f in findings.Where(f => f.IsDifference).Take(200))
            sb.AppendLine($"- {f.Id}: authored {(f.AuthoredReferences.Count == 0 ? "—" : string.Join("; ", f.AuthoredReferences.Select(Md)))} · generated {Md(f.GeneratedReference)} · rule {Md(f.Rule)}");
        return sb.ToString();
    }
}
