using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The documentation area a document describes. Structured composite comparison exists only where extraction has stable concept identity.</summary>
public enum DriftConcern { DataModel, Specification, Plan, Tasks, Constitution, Architecture, Overview, Messaging, Containers, Changelog, General, Other }

/// <summary>The state of one module + concern candidate. Absence on either side is neutral; ambiguity is never auto-resolved.</summary>
public enum DriftGroupState { ComparisonAvailable, GeneratedNotPresent, AuthoredNotPresent, MultipleGeneratedCandidates, StructuredComparisonNotSupported }

/// <summary>
/// One module + documentation-area candidate in one import: every deterministic authored contributor (same module, same concern, same import)
/// against the one generated document of that module and concern. Identity is import + module + concern + origin pairing — never a file name.
/// </summary>
public sealed record DriftCandidateGroup(
    string ImportId, string Module, DriftConcern Concern, DriftDocument? Generated, IReadOnlyList<DriftDocument> Contributors,
    IReadOnlyList<DriftDocument> GeneratedCandidates, DriftGroupState State, string Status)
{
    public string Key => $"{ImportId}|{Module.ToLowerInvariant()}|{Concern}|authored-composite↔generated";
    public string Title => $"{Module} · {DocumentationDriftComposite.ConcernLabel(Concern)} consistency";

    public string StateLabel => State switch
    {
        DriftGroupState.ComparisonAvailable => "Consistency comparison available",
        DriftGroupState.GeneratedNotPresent => "Consistency comparison unavailable · generated documentation not present",
        DriftGroupState.AuthoredNotPresent => "Consistency comparison unavailable · authored intent not present",
        DriftGroupState.MultipleGeneratedCandidates => "Review required — multiple generated candidates",
        _ => "Structured comparison not supported for this documentation area",
    };
}

/// <summary>Where one authored statement came from: the contributing artifact (exact revision/fingerprint) and the section it was stated in.</summary>
public sealed record CompositeProvenance(string DocumentId, string Path, string Fingerprint, string? Pack, string Section);

/// <summary>One authored statement about one concept attribute ("BarnType.Status · required = Required").</summary>
public sealed record CompositeStatement(string Value, CompositeProvenance Provenance);

/// <summary>An attribute whose authored statements disagree with no deterministic replacement: review required, never a winner.</summary>
public sealed record AuthoredIntentConflict(string Concept, string Attribute, IReadOnlyList<CompositeStatement> Statements)
{
    public const string Reason = "BirkNext cannot deterministically establish which statement supersedes the other.";
}

/// <summary>A deterministic replacement: the later contributor explicitly states the change. Both statements and their artifacts are kept.</summary>
public sealed record AuthoredReplacement(string Concept, string Attribute, CompositeStatement Previous, CompositeStatement Replacing, string Basis);

/// <summary>One field of the composite: the effective authored statement per attribute (when established) and every statement's provenance.</summary>
public sealed record CompositeField(string Entity, string Name, string? Type, FieldRequirement Requirement, bool PrimaryKey, IReadOnlyList<CompositeProvenance> Provenance, bool Conflicted);

public sealed record CompositeEntity(string Name, IReadOnlyList<CompositeProvenance> Provenance, IReadOnlyList<CompositeField> Fields);

public sealed record CompositeRelationship(string Source, string Target, IReadOnlyList<CompositeProvenance> Provenance);

/// <summary>
/// Composite authored intent for one module + concern: the deterministic union of the structured concepts its authored contributors state,
/// with full provenance. Not a concatenation of Markdown and never a winner-picks: a later feature pack adds to — it does not replace — the
/// earlier ones; an explicit change is applied only when stated; anything else contradictory is an authored conflict. Reproducible: the
/// fingerprint is derived from contributor fingerprints and the normalized concepts. Contents are references, never copies of the Markdown.
/// </summary>
public sealed record CompositeAuthoredIntent(
    string ImportId, string Module, DriftConcern Concern, IReadOnlyList<DriftDocument> Contributors,
    IReadOnlyList<CompositeEntity> Entities, IReadOnlyList<CompositeRelationship> Relationships,
    IReadOnlyList<AuthoredIntentConflict> Conflicts, IReadOnlyList<AuthoredReplacement> Replacements, int UnstructuredBlocks, string Fingerprint, int AlgorithmVersion)
{
    public int FieldCount => Entities.Sum(e => e.Fields.Count);
}

/// <summary>Result categories of a structured consistency comparison. A difference is a consistency discrepancy, never a statement about who is right.</summary>
public enum ConsistencyCategory { Consistent, AuthoredOnly, GeneratedOnly, FieldDifference, ConstraintDifference, RelationshipDifference, NotConcludedAuthoredConflict, NotComparable }

/// <summary>One concept compared, with the exact authored contributors and the generated document that support each side.</summary>
public sealed record ConsistencyRow(string Concept, ConsistencyCategory Category, string? Authored, string? Generated,
    IReadOnlyList<CompositeProvenance> AuthoredProvenance, string? GeneratedPath, string Explanation);

public sealed record DataModelConsistencyResult(CompositeAuthoredIntent Intent, DriftDocument Generated, IReadOnlyList<ConsistencyRow> Rows, int GeneratedEntities, int GeneratedFields, int GeneratedRelationships)
{
    public int Count(ConsistencyCategory c) => Rows.Count(r => r.Category == c);
    public int Discrepancies => Rows.Count(r => r.Category is ConsistencyCategory.FieldDifference or ConsistencyCategory.ConstraintDifference or ConsistencyCategory.RelationshipDifference);
}

public static partial class DocumentationDriftComposite
{
    public const int AlgorithmVersion = 1;
    public const string StructuredDataModel = "StructuredDataModel";

    public static string ConcernLabel(DriftConcern c) => c switch
    {
        DriftConcern.DataModel => "Data Model",
        DriftConcern.Overview => "Overview",
        DriftConcern.General => "README / General documentation",
        _ => c.ToString(),
    };

    /// <summary>Concern of an authored document from its artifact role; of a generated document from its generated-document kind. Never a name guess
    /// for a trusted pair: unrecognized documents are Other.</summary>
    public static DriftConcern ConcernOf(DriftDocument d)
    {
        if (d.Origin == DriftOrigin.Authored)
            return d.TypeLabel switch
            {
                "Data model" => DriftConcern.DataModel,
                "Specification" => DriftConcern.Specification,
                "Plan" => DriftConcern.Plan,
                "Tasks" => DriftConcern.Tasks,
                "Constitution" => DriftConcern.Constitution,
                _ => DriftConcern.Other,
            };
        var file = System.IO.Path.GetFileNameWithoutExtension(d.Path).ToLowerInvariant();
        return d.TypeLabel.Replace("Generated · ", "") switch
        {
            "DataModel" => DriftConcern.DataModel,
            "Architecture" => file == "containers" ? DriftConcern.Containers : DriftConcern.Architecture,
            "Messaging" => DriftConcern.Messaging,
            "Changelog" => DriftConcern.Changelog,
            "Overview" => file == "readme" ? DriftConcern.General : DriftConcern.Overview,
            _ => DriftConcern.Other,
        };
    }

    /// <summary>Concerns with stable structured concept identity (composite comparison is offered only for these).</summary>
    public static bool Structured(DriftConcern c) => c == DriftConcern.DataModel;

    // ── Candidates ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Module + concern candidates of one import: one per (module, concern), never one per authored file. Authored contributors are those of the
    /// same import whose module is established (the folder before <c>specs/</c>) and whose role is the concern; project-level documents and other
    /// modules never enter. Absence on either side is neutral; two generated documents for one module + concern are "Review required".
    /// </summary>
    public static IReadOnlyList<DriftCandidateGroup> CandidateGroups(IReadOnlyList<DriftDocument> catalog, IReadOnlyList<SddDocumentationDriftComparison> saved, string? importId,
        IReadOnlyCollection<string>? projectImports = null)
    {
        if (importId is null) return [];
        var current = catalog.Where(d => (d.ProjectImportId == importId || (d.ProjectImportId is { } other && projectImports?.Contains(other) == true)) && d.Supported).ToList();
        var generated = current.Where(d => d.Origin == DriftOrigin.Generated && d.Module is not null and not "(repository)")
            .GroupBy(d => (Module: d.Module!.ToLowerInvariant(), Concern: ConcernOf(d)))
            .Where(g => Structured(g.Key.Concern)).ToDictionary(g => g.Key, g => g.ToList());
        var authored = current.Where(d => d.Origin == DriftOrigin.Authored && d.ContentAvailable && Structured(ConcernOf(d)))
            .Select(d => (Doc: d, Module: DocumentationDrift.ModuleOf(d)))
            .Where(x => x.Module != Explorers.ArtifactModules.ProjectLevelLabel)
            .GroupBy(x => (Module: x.Module.ToLowerInvariant(), Concern: ConcernOf(x.Doc)))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Doc).OrderBy(PackOrder).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList());

        var groups = new List<DriftCandidateGroup>();
        foreach (var key in generated.Keys.Union(authored.Keys).OrderBy(k => k.Module, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Concern))
        {
            var gens = generated.GetValueOrDefault(key) ?? [];
            var contributors = authored.GetValueOrDefault(key) ?? [];
            var module = gens.FirstOrDefault()?.Module ?? DocumentationDrift.ModuleOf(contributors[0]);
            var state = gens.Count > 1 ? DriftGroupState.MultipleGeneratedCandidates
                : gens.Count == 0 ? DriftGroupState.GeneratedNotPresent
                : contributors.Count == 0 ? DriftGroupState.AuthoredNotPresent
                : !gens[0].ContentAvailable ? DriftGroupState.StructuredComparisonNotSupported
                : DriftGroupState.ComparisonAvailable;
            var single = gens.Count == 1 ? gens[0] : null;
            groups.Add(new(importId, module, key.Concern, single, contributors, gens, state, single is null ? "Not compared" : Status(saved, contributors, single)));
        }
        return groups;
    }

    /// <summary>"Comparison candidate" until a saved composite comparison of exactly these contributor fingerprints and this generated fingerprint exists.</summary>
    public static string Status(IReadOnlyList<SddDocumentationDriftComparison> saved, IReadOnlyList<DriftDocument> contributors, DriftDocument generated)
    {
        var match = SavedFor(saved, contributors, generated);
        return match is null ? "Comparison candidate" : match.Items.Count(i => i.Kind != nameof(ConsistencyCategory.Consistent)) == 0 ? "Consistent" : "Saved · differences found";
    }

    /// <summary>The saved structured comparison of exactly these contributors (fingerprints) against this generated document, or null — the one
    /// rule <see cref="Status"/> and the Drift cards share (no second review-status store).</summary>
    public static SddDocumentationDriftComparison? SavedFor(IReadOnlyList<SddDocumentationDriftComparison> saved, IReadOnlyList<DriftDocument> contributors, DriftDocument generated)
    {
        var set = contributors.Select(c => c.Fingerprint).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return saved.FirstOrDefault(s => s.ComparisonKind == StructuredDataModel && s.Right.Fingerprint == generated.Fingerprint
            && s.Contributors.Select(c => c.Fingerprint).OrderBy(f => f, StringComparer.Ordinal).SequenceEqual(set));
    }

    /// <summary>Spec-Kit feature order ("001-…" before "002-…") — used only to decide whether an explicitly stated change is the later statement.</summary>
    private static int PackOrder(DriftDocument d) =>
        Explorers.ArtifactModules.FeatureOf(d.Path) is { } pack && LeadingNumber().Match(pack) is { Success: true } m ? int.Parse(m.Value) : int.MaxValue;

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    // ── Data Model composite ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Words by which an authored data model explicitly states a change to an earlier statement.</summary>
    [GeneratedRegex(@"\b(changed?|changes|replaces?|replaced|now|endret|endres|erstatter|tidligere)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitChange();

    private sealed class FieldAccumulator
    {
        public string Entity = "", Name = "";
        public List<CompositeStatement> Types = [], Requirements = [], Keys = [];
        public List<CompositeProvenance> Provenance = [];
        public List<string> Descriptions = [];
    }

    public static CompositeAuthoredIntent BuildDataModel(string importId, string module, IReadOnlyList<(DriftDocument Document, string Text)> contributors, IDataModelAnalysisService parser)
    {
        var ordered = contributors.OrderBy(c => PackOrder(c.Document)).ThenBy(c => c.Document.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var entities = new Dictionary<string, (string Name, List<CompositeProvenance> Provenance, Dictionary<string, FieldAccumulator> Fields)>(StringComparer.OrdinalIgnoreCase);
        var relationships = new Dictionary<string, (string Source, string Target, List<CompositeProvenance> Provenance)>(StringComparer.OrdinalIgnoreCase);
        var unstructured = 0;
        foreach (var (document, text) in ordered)
        {
            var model = parser.Parse(text);
            unstructured += model.UnmappedSourceBlocks?.Count ?? 0;
            var pack = Explorers.ArtifactModules.FeatureOf(document.Path);
            foreach (var entity in model.Entities)
            {
                var name = DataModelIdentity.LogicalName(entity.Name);
                var key = DataModelIdentity.Transliterate(name);
                if (!entities.TryGetValue(key, out var e)) entities[key] = e = (name, [], new(StringComparer.OrdinalIgnoreCase));
                var provenance = new CompositeProvenance(document.Id, document.Path, document.Fingerprint, pack, name);
                e.Provenance.Add(provenance);
                foreach (var column in entity.Columns)
                {
                    var fieldKey = DataModelIdentity.Transliterate(column.Name);
                    if (!e.Fields.TryGetValue(fieldKey, out var f)) e.Fields[fieldKey] = f = new FieldAccumulator { Entity = name, Name = column.Name };
                    var p = provenance with { Section = $"{name}.{column.Name}" };
                    f.Provenance.Add(p);
                    if (NormalizeType(column.Type) is { } type) f.Types.Add(new(type, p));
                    var requirement = DataModelTesterView.Requirement(column);
                    if (requirement != FieldRequirement.Unknown) f.Requirements.Add(new(requirement.ToString(), p));
                    f.Descriptions.Add(column.Description ?? "");
                }
            }
            foreach (var r in model.Relationships)
            {
                var source = EntityOf(r.Source);
                var target = EntityOf(r.Target);
                if (source.Length == 0 || target.Length == 0) continue;
                var key = $"{DataModelIdentity.Transliterate(source)}→{DataModelIdentity.Transliterate(target)}";
                if (!relationships.TryGetValue(key, out var rel)) relationships[key] = rel = (source, target, []);
                rel.Provenance.Add(new(document.Id, document.Path, document.Fingerprint, pack, $"{source} → {target}"));
            }
        }

        var conflicts = new List<AuthoredIntentConflict>();
        var replacements = new List<AuthoredReplacement>();
        var compositeEntities = entities.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => new CompositeEntity(e.Name, e.Provenance,
            e.Fields.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f =>
            {
                var concept = $"{f.Entity}.{f.Name}";
                var type = Resolve(concept, "type", f.Types, f, conflicts, replacements);
                var requirement = Resolve(concept, "required", f.Requirements, f, conflicts, replacements);
                var conflicted = conflicts.Any(c => c.Concept == concept);
                return new CompositeField(f.Entity, f.Name, type, requirement is null ? FieldRequirement.Unknown : Enum.Parse<FieldRequirement>(requirement), false, f.Provenance, conflicted);
            }).ToList())).ToList();
        var compositeRelationships = relationships.Values.Select(r => new CompositeRelationship(r.Source, r.Target, r.Provenance)).ToList();

        var fingerprint = Fingerprint(ordered.Select(c => c.Document.Fingerprint), compositeEntities, compositeRelationships);
        return new(importId, module, DriftConcern.DataModel, ordered.Select(c => c.Document).ToList(), compositeEntities, compositeRelationships, conflicts, replacements, unstructured, fingerprint, AlgorithmVersion);
    }

    /// <summary>
    /// The effective authored value of one attribute. Equivalent statements merge. Differing statements resolve only when the LATER contributor
    /// (Spec-Kit feature order) explicitly states the change in its own description; otherwise they are an authored conflict and no value is chosen.
    /// </summary>
    private static string? Resolve(string concept, string attribute, List<CompositeStatement> statements, FieldAccumulator field,
        List<AuthoredIntentConflict> conflicts, List<AuthoredReplacement> replacements)
    {
        var distinct = statements.Select(s => s.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count <= 1) return distinct.FirstOrDefault();
        var last = statements[^1];
        var lastIndex = field.Provenance.FindLastIndex(p => p.DocumentId == last.Provenance.DocumentId);
        var lastDescription = lastIndex >= 0 && lastIndex < field.Descriptions.Count ? field.Descriptions[lastIndex] : "";
        var earlierDocuments = statements.Take(statements.Count - 1).Select(s => s.Provenance.DocumentId).ToHashSet();
        if (!earlierDocuments.Contains(last.Provenance.DocumentId) && ExplicitChange().IsMatch(lastDescription))
        {
            var previous = statements.Last(s => !string.Equals(s.Value, last.Value, StringComparison.OrdinalIgnoreCase));
            replacements.Add(new(concept, attribute, previous, last, $"The later contributor ({last.Provenance.Path}) explicitly states the change: \"{lastDescription.Trim()}\"."));
            return last.Value;
        }
        conflicts.Add(new(concept, attribute, statements));
        return null;
    }

    private static string EntityOf(string reference) => DataModelIdentity.LogicalName(reference.Split('.')[0]);

    /// <summary>Formatting-insensitive type identity ("uniqueidentifier"/"UNIQUEIDENTIFIER", "nvarchar( 50 )"/"nvarchar(50)").</summary>
    public static string? NormalizeType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? null : Regex.Replace(type.Trim().Trim('`'), @"\s+", "").ToLowerInvariant();

    private static string Fingerprint(IEnumerable<string> contributorFingerprints, IReadOnlyList<CompositeEntity> entities, IReadOnlyList<CompositeRelationship> relationships)
    {
        var sb = new StringBuilder($"v{AlgorithmVersion}|");
        foreach (var f in contributorFingerprints.OrderBy(f => f, StringComparer.Ordinal)) sb.Append(f).Append('|');
        foreach (var e in entities)
        {
            sb.Append(e.Name.ToLowerInvariant()).Append('{');
            foreach (var f in e.Fields) sb.Append(f.Name.ToLowerInvariant()).Append(':').Append(f.Type).Append(':').Append(f.Requirement).Append(';');
            sb.Append('}');
        }
        foreach (var r in relationships.OrderBy(r => r.Source).ThenBy(r => r.Target)) sb.Append(r.Source.ToLowerInvariant()).Append('>').Append(r.Target.ToLowerInvariant()).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    // ── Structured comparison ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares composite authored intent with the generated Data Model concept by concept. Generated documentation that does not state an
    /// attribute is "not stated" (never a difference). Concepts with an authored conflict are not concluded. <paramref name="contributionOnly"/>:
    /// a single contribution is compared only on the concepts it states — generated-only concepts are then out of scope, not discrepancies.
    /// </summary>
    public static DataModelConsistencyResult CompareDataModel(CompositeAuthoredIntent intent, DriftDocument generated, string generatedText, IDataModelAnalysisService parser, bool contributionOnly = false)
    {
        var model = DataModelGeneratedSemantics.Apply(parser.Parse(generatedText));
        var genEntities = model.Entities.ToDictionary(e => DataModelIdentity.Transliterate(DataModelIdentity.LogicalName(e.Name)), StringComparer.OrdinalIgnoreCase);
        var rows = new List<ConsistencyRow>();
        var path = generated.Path;
        var conflictConcepts = intent.Conflicts.Select(c => c.Concept).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in intent.Entities)
        {
            var key = DataModelIdentity.Transliterate(entity.Name);
            if (!genEntities.TryGetValue(key, out var gen))
            {
                rows.Add(new(entity.Name, ConsistencyCategory.AuthoredOnly, "Entity", null, entity.Provenance, path,
                    "Expected in authored documentation but not represented in the generated Data Model documentation. This is a documentation consistency discrepancy, not proof the entity is absent from source code."));
                continue;
            }
            var genColumns = gen.Columns.ToDictionary(c => DataModelIdentity.Transliterate(c.Name), StringComparer.OrdinalIgnoreCase);
            var entityConsistent = true;
            foreach (var field in entity.Fields)
            {
                var concept = $"{entity.Name}.{field.Name}";
                if (conflictConcepts.Contains(concept))
                {
                    rows.Add(new(concept, ConsistencyCategory.NotConcludedAuthoredConflict, null, null, field.Provenance, path,
                        "Comparison not concluded for this concept because authored intent is internally inconsistent."));
                    entityConsistent = false;
                    continue;
                }
                if (!genColumns.TryGetValue(DataModelIdentity.Transliterate(field.Name), out var column))
                {
                    rows.Add(new(concept, ConsistencyCategory.AuthoredOnly, "Field", null, field.Provenance, path,
                        "Stated in authored intent but not represented in the generated documentation. Not proof the field is absent from source code."));
                    entityConsistent = false;
                    continue;
                }
                var genType = NormalizeType(column.Type);
                if (field.Type is { } at && genType is { } gt && at != gt)
                {
                    rows.Add(new(concept, ConsistencyCategory.FieldDifference, at, gt, field.Provenance, path,
                        "The authored intent and generated documentation describe this field's type differently."));
                    entityConsistent = false;
                }
                var genRequirement = DataModelTesterView.Requirement(column);
                if (field.Requirement != FieldRequirement.Unknown && genRequirement != FieldRequirement.Unknown && field.Requirement != genRequirement)
                {
                    rows.Add(new(concept, ConsistencyCategory.ConstraintDifference, field.Requirement.ToString(), genRequirement.ToString(), field.Provenance, path,
                        "The authored intent and generated documentation describe this field differently."));
                    entityConsistent = false;
                }
            }
            if (!contributionOnly)
                foreach (var column in gen.Columns.Where(c => !entity.Fields.Any(f => DataModelIdentity.Match(f.Name, c.Name) is not null)))
                    rows.Add(new($"{entity.Name}.{column.Name}", ConsistencyCategory.GeneratedOnly, null, "Field", [], path,
                        "Implementation-derived field not represented in authored intent. Not automatically a missing-specification defect."));
            if (entityConsistent)
                rows.Add(new(entity.Name, ConsistencyCategory.Consistent, "Entity", "Entity", entity.Provenance, path, "Consistent with the generated documentation for every stated field."));
        }
        if (!contributionOnly)
            foreach (var gen in model.Entities.Where(g => !intent.Entities.Any(e => DataModelIdentity.Match(e.Name, g.Name) is not null)))
                rows.Add(new(DataModelIdentity.LogicalName(gen.Name), ConsistencyCategory.GeneratedOnly, null, "Entity", [], path,
                    "Implementation-derived concept not represented in authored intent. Not automatically a missing-specification defect."));

        // Relationships by (source entity, target entity) identity.
        var genRelationships = model.Relationships.Select(r => (Source: EntityOf(r.Source), Target: EntityOf(r.Target))).Where(r => r.Source.Length > 0 && r.Target.Length > 0).ToList();
        foreach (var r in intent.Relationships)
        {
            var found = genRelationships.Any(g => DataModelIdentity.Match(g.Source, r.Source) is not null && DataModelIdentity.Match(g.Target, r.Target) is not null);
            if (!found && model.RelationshipEvidence != DataModelEvidenceState.NotRepresented)
                rows.Add(new($"{r.Source} → {r.Target}", ConsistencyCategory.RelationshipDifference, "Relationship", null, r.Provenance, path,
                    "The authored intent states this relationship; the generated documentation does not represent it."));
        }
        if (intent.Relationships.Count > 0 && model.RelationshipEvidence == DataModelEvidenceState.NotRepresented)
            rows.Add(new("Relationships", ConsistencyCategory.NotComparable, null, null, [], path, "The generated documentation does not represent relationships, so they cannot be compared."));

        return new(intent, generated, rows, model.Entities.Count, model.Entities.Sum(e => e.Columns.Count), model.Relationships.Count);
    }

    /// <summary>Items in the existing saved-comparison shape (Section = concept, Kind = category), so saving and reopening keep working unchanged.</summary>
    public static DriftComparisonResult ToResult(DataModelConsistencyResult result)
    {
        var items = result.Rows.Select(r => new SddDriftItem
        {
            Kind = r.Category.ToString(), Section = r.Concept, Detail = r.Explanation,
            Left = [.. new[] { r.Authored }.Where(v => v is not null)!, .. r.AuthoredProvenance.Select(p => $"{p.Path} · {p.Section}")],
            Right = [.. new[] { r.Generated }.Where(v => v is not null)!, .. new[] { r.GeneratedPath }.Where(v => v is not null)!],
        }).ToList();
        var counts = Enum.GetValues<ConsistencyCategory>().ToDictionary(c => c.ToString(), c => result.Count(c));
        counts["AuthoredConflicts"] = result.Intent.Conflicts.Count;
        var outcome = result.Discrepancies == 0 && result.Count(ConsistencyCategory.AuthoredOnly) == 0 && result.Intent.Conflicts.Count == 0
            ? "Consistent with the generated documentation for every compared concept"
            : $"{result.Discrepancies} consistency discrepancies · {result.Count(ConsistencyCategory.GeneratedOnly)} generated-only · {result.Count(ConsistencyCategory.AuthoredOnly)} authored-only · {result.Intent.Conflicts.Count} authored conflicts";
        return new(DriftIntent.CompositeConsistency, DocumentationDrift.SameRevision, outcome, items, counts);
    }

    /// <summary>Saves a composite comparison with every contributor's exact identity and fingerprint; a later import never changes it.</summary>
    public static SddDocumentationDriftComparison Save(SddLifecycleState state, DataModelConsistencyResult result, string? scope, DateTimeOffset now, bool contributionOnly = false)
    {
        var drift = ToResult(result);
        var saved = new SddDocumentationDriftComparison
        {
            CreatedAt = now, Intent = (contributionOnly ? DriftIntent.ContributionConsistency : DriftIntent.CompositeConsistency).ToString(), Scope = $"{result.Intent.Module} · Data Model",
            RevisionContext = drift.RevisionContext, Left = DocumentationDrift.Side(result.Intent.Contributors[0]), Right = DocumentationDrift.Side(result.Generated),
            Contributors = [.. result.Intent.Contributors.Select(DocumentationDrift.Side)], Module = result.Intent.Module, Concern = DriftConcern.DataModel.ToString(),
            ComparisonKind = StructuredDataModel, CompositeFingerprint = result.Intent.Fingerprint, AlgorithmVersion = AlgorithmVersion,
            Outcome = drift.Outcome, Counts = new(drift.Counts), Items = [.. drift.Items], AnalysisVersion = DocumentationDrift.AnalysisVersion, WorkspaceScope = scope,
        };
        state.DocumentationDriftComparisons.Insert(0, saved);
        if (state.DocumentationDriftComparisons.Count > 50) state.DocumentationDriftComparisons.RemoveAt(state.DocumentationDriftComparisons.Count - 1);
        return saved;
    }

    public static string CategoryLabel(ConsistencyCategory c) => c switch
    {
        ConsistencyCategory.Consistent => "Consistent",
        ConsistencyCategory.AuthoredOnly => "Authored only",
        ConsistencyCategory.GeneratedOnly => "Generated only",
        ConsistencyCategory.FieldDifference => "Field difference",
        ConsistencyCategory.ConstraintDifference => "Constraint difference",
        ConsistencyCategory.RelationshipDifference => "Relationship difference",
        ConsistencyCategory.NotConcludedAuthoredConflict => "Not concluded · authored conflict",
        _ => "Not comparable",
    };
}
