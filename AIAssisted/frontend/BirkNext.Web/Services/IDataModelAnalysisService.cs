using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IDataModelAnalysisService
{
    DataModelDocument Parse(string markdown);

    IEnumerable<DataEntity>       FilterEntities(IEnumerable<DataEntity> entities, string query);
    IEnumerable<DataRelationship> FilterRelationships(IEnumerable<DataRelationship> rels, string query);
    IEnumerable<DataIndex>        FilterIndexes(IEnumerable<DataIndex> indexes, string query);
    IEnumerable<DataConstraint>   FilterConstraints(IEnumerable<DataConstraint> constraints, string query);

    bool IsSensitiveColumn(DataColumn column);
}
