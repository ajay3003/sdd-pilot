using System.Text.RegularExpressions;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>One document of a Plan document set as it was inspected: where it is, why it belongs, and what the shared plan parser extracted.</summary>
public sealed record PlanSetDocument(string Path, string Kind, string ClassLabel, string Reason, bool IsPrimary, bool PackScoped, PlanDocument Parsed, string? Text = null);

/// <summary>An architecture decision with the document that states it.</summary>
public sealed record PlanSetDecision(PlanArchitectureDecision Decision, string Path, bool IsPrimary);

/// <summary>A statement about testing (what, how, with which evidence) with the document that states it.</summary>
public sealed record PlanSetStatement(string Text, string Path, string Source);

/// <summary>
/// The Plan of an Artifact Pack as a document set: the primary plan plus the supporting documents the canonical inventory assigns to the plan
/// (research, quickstart, contracts, decisions it references or that sit in the pack, and module-shared plan documents). Every extracted item
/// keeps its document. Interpretation only — discovery belongs to <see cref="ArtifactPackInventory"/>, parsing to the plan parser.
/// </summary>
public sealed partial class PlanDocumentSet
{
    public IReadOnlyList<PlanSetDocument> Documents { get; }
    public PlanSetDocument Primary => Documents[0];
    public IReadOnlyList<PlanSetDocument> Supporting => [.. Documents.Skip(1)];

    private PlanDocumentSet(IReadOnlyList<PlanSetDocument> documents) => Documents = documents;

    /// <summary>The primary plan alone (no inventory available): every answer is about the primary plan only.</summary>
    public static PlanDocumentSet Single(PlanDocument primary, string path = "plan.md", string? text = null) =>
        new([new PlanSetDocument(path, "Plan", "Primary", "Primary Plan of the Artifact Pack", true, true, primary, text)]);

    /// <summary>The set for <paramref name="primary"/> from the inventory's Plan family (supporting documents parsed with the same parser).</summary>
    public static PlanDocumentSet From(PlanDocument primary, ArtifactPackInventory inventory, IPlanAnalysisService parser, string? primaryText = null)
    {
        var primaryDoc = inventory.PrimaryOf(WorkspaceArtifactType.Plan);
        var docs = new List<PlanSetDocument>
        {
            new(primaryDoc?.Path ?? "plan.md", "Plan", primaryDoc?.ClassLabel ?? "Primary", primaryDoc?.Reason ?? "Primary Plan of the Artifact Pack", true, true, primary,
                primaryText ?? primaryDoc?.Text),
        };
        // The Plan family, plus the pack's own decision and architecture documents whichever document references them: decisions are plan content.
        var members = inventory.Family(WorkspaceArtifactType.Plan)
            .Concat(inventory.Supporting.Where(d => d.Class == PackDocumentClass.SupportingPackLocal && d.Kind is "Decision (ADR)" or "Architecture"))
            .DistinctBy(d => d.Path);
        foreach (var d in members.Where(d => !d.IsPrimary && d.IsAuthored && !string.IsNullOrWhiteSpace(d.Text)))
        {
            PlanDocument parsed;
            try { parsed = parser.Parse(d.Text!); }
            catch (Exception) { continue; }
            docs.Add(new PlanSetDocument(d.Path, d.Kind, d.ClassLabel, d.Reason, false, d.Class == PackDocumentClass.SupportingPackLocal, parsed, d.Text));
        }
        return new PlanDocumentSet(docs);
    }

    /// <summary>Architecture decisions of every document, primary first; one decision stated twice (same title) is listed once.</summary>
    public IReadOnlyList<PlanSetDecision> Decisions =>
        [.. Documents.SelectMany(d => d.Parsed.ArchitectureDecisions.Select(a => new PlanSetDecision(a, d.Path, d.IsPrimary)))
            .DistinctBy(d => d.Decision.Title.Trim().ToLowerInvariant())];

    public int PrimaryDecisionCount => Primary.Parsed.ArchitectureDecisions.Count;

    /// <summary>Testing tools named in any document, with the first document naming each.</summary>
    public IReadOnlyList<(string Tool, string Path)> TestingTools =>
        [.. Documents.SelectMany(d => (d.Parsed.TestingInfo?.Frameworks ?? []).Select(f => (Tool: f, d.Path))).DistinctBy(t => t.Tool.ToLowerInvariant())];

    /// <summary>
    /// Explicit testing statements: the testing section's statements of every document, and decisions about testing ("Decision: Testing
    /// Stack"). A tool name alone is never a statement.
    /// </summary>
    public IReadOnlyList<PlanSetStatement> TestingStatements =>
        [.. Documents.SelectMany(d => PlanTesterPresentation.TestingStatements(d.Parsed).Select(t => new PlanSetStatement(t, d.Path, "Testing section"))
            .Concat(d.Parsed.ArchitectureDecisions.Where(a => TestingWord().IsMatch(a.Title))
                .Select(a => new PlanSetStatement($"{a.Title}: {Shorten(a.Decision)}", d.Path, "Decision"))))
            .DistinctBy(s => s.Text)];

    /// <summary>Statements about verification or validation activities among the testing statements.</summary>
    public IReadOnlyList<PlanSetStatement> VerificationActivities => [.. TestingStatements.Where(s => VerificationWord().IsMatch(s.Text))];

    /// <summary>Success-criterion / acceptance references ("SC-003") written in any document, with the first document naming each.</summary>
    public IReadOnlyList<(string Id, string Path)> AcceptanceReferences =>
        [.. Documents.SelectMany(d => SuccessCriterion().Matches(d.Text ?? "")
            .Select(m => (Id: $"SC-{int.Parse(m.Groups[1].Value):000}", d.Path))).DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal)];

    /// <summary>What is missing across the whole set (never from the primary plan alone).</summary>
    public IReadOnlyList<string> TestingGaps
    {
        get
        {
            var gaps = new List<string>();
            if (TestingStatements.Count == 0)
                gaps.Add(TestingTools.Count > 0
                    ? $"Testing tools are named, but none of the {Documents.Count} inspected Plan document{(Documents.Count == 1 ? "" : "s")} states what to test, how, or with which evidence."
                    : $"None of the {Documents.Count} inspected Plan document{(Documents.Count == 1 ? "" : "s")} describes a testing approach.");
            if (AcceptanceReferences.Count == 0) gaps.Add("No success-criterion (SC-) references in the inspected Plan documents.");
            return gaps;
        }
    }

    /// <summary>
    /// Adds the requirement IDs written in the pack's own supporting plan documents to <paramref name="plan"/> (a freshly parsed primary plan),
    /// labelled with their document ("research.md § Testing"). Shared module documents are not added: their FR-001 is not this pack's FR-001.
    /// </summary>
    public static void AddPackScopedMentions(PlanDocument plan, ArtifactPackInventory? inventory)
    {
        if (inventory is null) return;
        foreach (var d in inventory.PackScopedFamily(WorkspaceArtifactType.Plan).Where(d => !d.IsPrimary && !string.IsNullOrWhiteSpace(d.Text)))
            foreach (var (id, headings) in PlanAnalysisService.RequirementMentionsOf(d.Text!))
            {
                if (!plan.RequirementMentions.TryGetValue(id, out var list)) plan.RequirementMentions[id] = list = [];
                foreach (var heading in headings.DefaultIfEmpty(""))
                {
                    var label = heading is "" or "Plan" ? d.FileName : $"{d.FileName} {heading}";
                    if (!list.Contains(label)) list.Add(label);
                }
            }
    }

    /// <summary>
    /// Widens a freshly parsed primary plan (owned by one review run) to the document set: supporting decisions are added with their document,
    /// testing statements in supporting documents count as testing information, and the pack's own supporting requirement mentions are added.
    /// Phases, gates, constitution checks, risks and constraints stay the primary plan's own.
    /// </summary>
    public void ExtendForReview(PlanDocument plan, ArtifactPackInventory? inventory)
    {
        foreach (var d in Decisions.Where(d => !d.IsPrimary && !plan.ArchitectureDecisions.Any(a => string.Equals(a.Title, d.Decision.Title, StringComparison.OrdinalIgnoreCase))))
            plan.ArchitectureDecisions.Add(new PlanArchitectureDecision
            {
                Provenance = d.Decision.Provenance, Id = d.Decision.Id, Title = d.Decision.Title, Context = d.Decision.Context, Decision = d.Decision.Decision,
                Rationale = d.Decision.Rationale, Consequences = d.Decision.Consequences, RawText = d.Decision.RawText, SourceDocument = d.Path,
            });
        plan.Health.TotalArchitectureDecisions = plan.ArchitectureDecisions.Count;
        plan.Health.HasArchitecture |= plan.ArchitectureDecisions.Count > 0;
        plan.Health.HasTestingInfo |= TestingStatements.Count > 0;
        AddPackScopedMentions(plan, inventory);
    }

    private static string Shorten(string text) => text.Length > 200 ? text[..197] + "…" : text;

    [GeneratedRegex(@"\b(test|testing|tests|verification|qa)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestingWord();

    [GeneratedRegex(@"\b(verif\w*|validat\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerificationWord();

    [GeneratedRegex(@"\bSC-?(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuccessCriterion();
}
