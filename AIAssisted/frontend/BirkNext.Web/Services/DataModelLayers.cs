using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Where a structure of the derived aggregate (the Combined view) comes from.</summary>
public enum DataModelLayerOrigin { SharedBase, Pack, OverriddenInPack }

/// <summary>
/// A pack's data model over the shared base data model of its module (or project). Both stay separate artifacts; the aggregate is a
/// derived, read-only combination: base structures, plus the pack's own, where a pack structure with the same logical identity overrides the
/// base one and is marked so — never silently merged field by field. Tester-facing names: Feature model, Shared model, Combined view.
/// </summary>
public static class DataModelLayers
{
    public const string FeatureLabel = "Feature model";
    public const string SharedLabel = "Shared model";
    public const string CombinedLabel = "Combined view";
    public const string FeatureHelper = "This feature pack's authored data model.";
    public const string SharedHelper = "Common data model used across feature packs in this module.";
    public const string CombinedHelper = "Read-only view of the feature model and shared model. It does not create a new authoritative data model.";
    public const string CombinedOrigin = "Combined view — derived/read-only (feature model + shared model; not an authored artifact)";
    public const string GeneratedOrigin = "Generated documentation — derived, not authoritative implementation evidence";

    public static string Label(DataModelLayerOrigin origin) => origin switch
    {
        DataModelLayerOrigin.SharedBase => SharedLabel,
        DataModelLayerOrigin.Pack => FeatureLabel,
        _ => "Feature + shared model",
    };

    /// <summary>autodoc/ and generated-docs/ documents are generated documentation — derived, never authored intent or implementation truth.</summary>
    public static bool IsGenerated(string? path) => path is { } p
        && (p.Contains("/autodoc/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("autodoc/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("generated-docs/", StringComparison.OrdinalIgnoreCase));

    public static (DataModelDocument Aggregate, IReadOnlyDictionary<string, DataModelLayerOrigin> Origins) Aggregate(DataModelDocument shared, DataModelDocument pack)
    {
        var origins = new Dictionary<string, DataModelLayerOrigin>(StringComparer.OrdinalIgnoreCase);
        bool InPack(string name) => pack.Entities.Any(p => DataModelIdentity.Match(p.Name, name) is not null);
        var entities = new List<DataEntity>();
        foreach (var entity in shared.Entities.Where(e => !InPack(e.Name)))
        {
            entities.Add(entity);
            origins[entity.Name] = DataModelLayerOrigin.SharedBase;
        }
        foreach (var entity in pack.Entities)
        {
            entities.Add(entity);
            origins[entity.Name] = shared.Entities.Any(s => DataModelIdentity.Match(s.Name, entity.Name) is not null) ? DataModelLayerOrigin.OverriddenInPack : DataModelLayerOrigin.Pack;
        }
        var packEnums = pack.Enums.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggregate = new DataModelDocument
        {
            Title = $"{CombinedLabel}: {shared.Title} + {pack.Title}",
            Entities = entities,
            Enums = [.. shared.Enums.Where(e => !packEnums.Contains(e.Name)), .. pack.Enums],
            Relationships = [.. shared.Relationships.Where(r => entities.Any(e => SameEntity(r.Source, e.Name)) && !pack.Entities.Any(n => SameEntity(r.Source, n.Name))), .. pack.Relationships],
            Indexes = [.. shared.Indexes, .. pack.Indexes],
            Constraints = [.. shared.Constraints, .. pack.Constraints],
            ColumnEvidence = pack.ColumnEvidence,
            RelationshipEvidence = pack.RelationshipEvidence,
            IndexEvidence = pack.IndexEvidence,
            ConstraintEvidence = pack.ConstraintEvidence,
        };
        return (aggregate, origins);
    }

    /// <summary>
    /// The Combined view's logical structures: one per logical identity, each origin's definition kept, the feature one effective, and the
    /// field differences between the two origins listed for review. Structures are grouped only on an exact logical name or the project's
    /// transliteration rule — never on similarity.
    /// </summary>
    public static IReadOnlyList<LogicalStructure> Structures(DataModelDocument shared, DataModelDocument pack)
    {
        var result = new List<LogicalStructure>();
        var usedShared = new HashSet<DataEntity>();
        foreach (var feature in pack.Entities)
        {
            var match = shared.Entities.Where(s => !usedShared.Contains(s)).Select(s => (Entity: s, Match: DataModelIdentity.Match(s.Name, feature.Name)))
                .Where(x => x.Match is not null).OrderBy(x => x.Match).FirstOrDefault();
            if (match.Entity is { } sharedEntity) usedShared.Add(sharedEntity);
            result.Add(new LogicalStructure(DataModelIdentity.LogicalName(feature.Name), feature, match.Entity, match.Match,
                match.Entity is null ? [] : Differences(feature, match.Entity)));
        }
        foreach (var sharedOnly in shared.Entities.Where(s => !usedShared.Contains(s)))
            result.Add(new LogicalStructure(DataModelIdentity.LogicalName(sharedOnly.Name), null, sharedOnly, null, []));
        return [.. result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<OriginDifference> Differences(DataEntity feature, DataEntity shared)
    {
        var differences = new List<OriginDifference>();
        var matched = new HashSet<DataColumn>();
        foreach (var column in feature.Columns)
        {
            var other = shared.Columns.FirstOrDefault(c => !matched.Contains(c) && DataModelIdentity.Match(c.Name, column.Name) is not null);
            if (other is null) { differences.Add(new(column.Name, "Only in the feature model")); continue; }
            matched.Add(other);
            var (a, b) = (DataModelTesterView.Requirement(column), DataModelTesterView.Requirement(other));
            if (a != FieldRequirement.Unknown && b != FieldRequirement.Unknown && a != b)
                differences.Add(new(column.Name, $"Feature model says {a.ToString().ToLowerInvariant()}, shared model says {b.ToString().ToLowerInvariant()}"));
        }
        differences.AddRange(shared.Columns.Where(c => !matched.Contains(c)).Select(c => new OriginDifference(c.Name, "Only in the shared model")));
        return differences;
    }

    private static bool SameEntity(string reference, string entity) =>
        reference.Equals(entity, StringComparison.OrdinalIgnoreCase) || reference.StartsWith(entity + ".", StringComparison.OrdinalIgnoreCase)
        || DataModelIdentity.Match(reference.Split('.')[0], entity) is not null;
}
