using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Where a structure of the derived aggregate comes from.</summary>
public enum DataModelLayerOrigin { SharedBase, Pack, OverriddenInPack }

/// <summary>
/// A pack's data model over the shared base data model of its module (or project). Both stay separate authored artifacts; the aggregate is a
/// derived, read-only combination: base structures, plus the pack's own, where a pack structure with the same name overrides the base one and
/// is marked so — never silently merged field by field.
/// </summary>
public static class DataModelLayers
{
    public static string Label(DataModelLayerOrigin origin) => origin switch
    {
        DataModelLayerOrigin.SharedBase => "Shared base",
        DataModelLayerOrigin.Pack => "Pack model",
        _ => "Overridden in pack",
    };

    public static (DataModelDocument Aggregate, IReadOnlyDictionary<string, DataModelLayerOrigin> Origins) Aggregate(DataModelDocument shared, DataModelDocument pack)
    {
        var origins = new Dictionary<string, DataModelLayerOrigin>(StringComparer.OrdinalIgnoreCase);
        var packNames = pack.Entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entities = new List<DataEntity>();
        foreach (var entity in shared.Entities.Where(e => !packNames.Contains(e.Name)))
        {
            entities.Add(entity);
            origins[entity.Name] = DataModelLayerOrigin.SharedBase;
        }
        var sharedNames = shared.Entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in pack.Entities)
        {
            entities.Add(entity);
            origins[entity.Name] = sharedNames.Contains(entity.Name) ? DataModelLayerOrigin.OverriddenInPack : DataModelLayerOrigin.Pack;
        }
        var packEnums = pack.Enums.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggregate = new DataModelDocument
        {
            Title = $"Derived aggregate: {shared.Title} + {pack.Title}",
            Entities = entities,
            Enums = [.. shared.Enums.Where(e => !packEnums.Contains(e.Name)), .. pack.Enums],
            Relationships = [.. shared.Relationships.Where(r => entities.Any(e => SameEntity(r.Source, e.Name)) && !packNames.Any(n => SameEntity(r.Source, n))), .. pack.Relationships],
            Indexes = [.. shared.Indexes, .. pack.Indexes],
            Constraints = [.. shared.Constraints, .. pack.Constraints],
            ColumnEvidence = pack.ColumnEvidence,
            RelationshipEvidence = pack.RelationshipEvidence,
            IndexEvidence = pack.IndexEvidence,
            ConstraintEvidence = pack.ConstraintEvidence,
        };
        return (aggregate, origins);
    }

    private static bool SameEntity(string reference, string entity) =>
        reference.Equals(entity, StringComparison.OrdinalIgnoreCase) || reference.StartsWith(entity + ".", StringComparison.OrdinalIgnoreCase);
}
