using BirkNext.DataModelEvidence;
using DocumentedEntity = BirkNext.Web.Models.DataEntity;
using DocumentedModel = BirkNext.Web.Models.DataModelDocument;
using SourceEntity = BirkNext.DataModelEvidence.DataEntity;

namespace BirkNext.Web.Services;

/// <summary>Neutral review states for a documented-vs-source difference: no severity score, never a defect by itself.</summary>
public enum SourceDifferenceState { Review, PotentialMismatch, Unknown }

public sealed record SourceStructureMatch(string Documented, SourceEntity Source, DataModelNameMatch Match);

public sealed record SourceFieldDifference(string Structure, string Field, string Kind, SourceDifferenceState State);

/// <summary>
/// The documented data model against the persistence model Source Analysis extracted. Names match exactly or through the project's
/// transliteration rule (typed, reported as such); anything else is reported as not matched — never fuzzy. The comparison is scoped to the
/// source stores the documented structures were found in; framework tables (Wolverine, EF migration history) and other stores stay in the
/// technical source evidence. Source-derived schema is not the deployed database; no live database or row data is read.
/// </summary>
public sealed record DataModelSourceComparison(
    IReadOnlyList<string> Documented,
    IReadOnlyList<SourceStructureMatch> Matched,
    IReadOnlyList<string> NotMatched,
    IReadOnlyList<string> ScopeStores,
    IReadOnlyList<SourceEntity> InScope,
    int OutOfScope,
    int FrameworkTables,
    int NullabilityLayerDifferences,
    int ExplicitMappings,
    int MigrationsDeclared,
    IReadOnlyList<SourceFieldDifference> FieldDifferences)
{
    public static bool IsFramework(SourceEntity entity) =>
        entity.Table.StartsWith("wolverine_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(entity.Schema, "wolverine", StringComparison.OrdinalIgnoreCase)
        || entity.Table.Equals("__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase);

    public static DataModelSourceComparison Compare(IReadOnlyList<string> documentedNames, DocumentedModel? documented, IReadOnlyList<SourceEntity> source,
        IReadOnlyList<DataMigration> migrations)
    {
        var names = documentedNames.Select(DataModelIdentity.LogicalName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = source.Where(e => !IsFramework(e)).ToList();
        var matched = new List<SourceStructureMatch>();
        var notMatched = new List<string>();
        foreach (var name in names)
        {
            var hit = candidates.Select(e => (Entity: e, Match: DataModelIdentity.Match(name, e.Entity ?? e.Table) ?? (e.Entity is null ? null : DataModelIdentity.Match(name, e.Table))))
                .Where(x => x.Match is not null).OrderBy(x => x.Match).FirstOrDefault();
            if (hit.Entity is null) notMatched.Add(name);
            else matched.Add(new SourceStructureMatch(name, hit.Entity, hit.Match!.Value));
        }
        var stores = matched.Select(m => m.Source.Store).Distinct(StringComparer.Ordinal).ToList();
        var inScope = stores.Count == 0 ? candidates : [.. candidates.Where(e => stores.Contains(e.Store, StringComparer.Ordinal))];
        var differences = new List<SourceFieldDifference>();
        if (documented is not null)
            foreach (var match in matched)
                if (documented.Entities.FirstOrDefault(e => DataModelIdentity.Match(e.Name, match.Documented) is not null) is { } entity)
                    differences.AddRange(DifferencesOf(match.Documented, entity, match.Source));
        return new DataModelSourceComparison(names, matched, notMatched, stores, inScope, source.Count - inScope.Count - source.Count(IsFramework), source.Count(IsFramework),
            inScope.Sum(e => e.Fields.Count(f => f.NullabilityDiffers)), inScope.Count(e => e.Mapping == DataMappingConfidence.Explicit),
            stores.Count == 0 ? migrations.Count : migrations.Count(m => stores.Contains(m.Store, StringComparer.Ordinal)), differences);
    }

    private static IEnumerable<SourceFieldDifference> DifferencesOf(string structure, DocumentedEntity documented, SourceEntity source)
    {
        foreach (var column in documented.Columns)
        {
            var field = source.Fields.FirstOrDefault(f => DataModelIdentity.Match(column.Name, f.Property ?? f.Column) is not null || DataModelIdentity.Match(column.Name, f.Column) is not null);
            if (field is null) { yield return new(structure, column.Name, "Not found in source evidence", SourceDifferenceState.Review); continue; }
            var authored = DataModelTesterView.Requirement(column);
            var implemented = field.DbNullable == false || field.OrmRequired == true || field.IsKey ? FieldRequirement.Required
                : (field.DbNullable ?? field.LanguageNullable) == true ? FieldRequirement.Optional : FieldRequirement.Unknown;
            if (authored == FieldRequirement.Required && implemented == FieldRequirement.Optional)
                yield return new(structure, column.Name, "Authored says required, source says nullable", SourceDifferenceState.PotentialMismatch);
            else if (authored == FieldRequirement.Optional && implemented == FieldRequirement.Required)
                yield return new(structure, column.Name, "Authored says optional, source says required", SourceDifferenceState.PotentialMismatch);
            else if (authored == FieldRequirement.Unknown && implemented != FieldRequirement.Unknown)
                yield return new(structure, column.Name, "Unknown in authored model", SourceDifferenceState.Unknown);
            else if (authored != FieldRequirement.Unknown && implemented == FieldRequirement.Unknown)
                yield return new(structure, column.Name, "Unknown in source evidence", SourceDifferenceState.Unknown);
            if (DataModelTesterView.Length(column) is { Text: { } } && MaxOf(column) is { } documentedMax && field.MaxLength is { } sourceMax && documentedMax != sourceMax)
                yield return new(structure, column.Name, $"Type representation difference: length {documentedMax} documented, {sourceMax} in source", SourceDifferenceState.Review);
        }
    }

    private static int? MaxOf(Models.DataColumn column) =>
        System.Text.RegularExpressions.Regex.Match($"{column.Type} {column.Description}", @"\(\s*(\d+)\s*\)|LEN\s*=\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
            ? int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    public static string StateLabel(SourceDifferenceState state) => state switch
    {
        SourceDifferenceState.PotentialMismatch => "Potential mismatch",
        SourceDifferenceState.Unknown => "Unknown",
        _ => "Review",
    };
}
