using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Data model semantics for generated documentation (autodoc). A generator that does not write nullability, keys, relationships, indexes,
/// classifications or requirement links has not stated them: that is unknown evidence, not a schema defect. Contradictions the document does
/// state (a relationship to an undefined structure) stay findings. The authored model is never adjusted.
/// </summary>
public static class DataModelGeneratedSemantics
{
    public const string NotStated = "Not stated in generated documentation";

    /// <summary>Marks <paramref name="document"/> (a fresh parse of a generated document) as generated and moves omission findings out of the findings.</summary>
    public static DataModelDocument Apply(DataModelDocument document)
    {
        document.IsGeneratedDocumentation = true;
        // A generated artifact never positively establishes that relationships, indexes or constraints do not apply: they are not represented.
        static DataModelEvidenceState NotRepresented(DataModelEvidenceState s) => s == DataModelEvidenceState.NotApplicable ? DataModelEvidenceState.NotRepresented : s;
        document.RelationshipEvidence = NotRepresented(document.RelationshipEvidence);
        document.IndexEvidence = NotRepresented(document.IndexEvidence);
        document.ConstraintEvidence = NotRepresented(document.ConstraintEvidence);
        var omissions = document.Findings.Where(IsOmission(document)).ToList();
        foreach (var omission in omissions) document.Findings.Remove(omission);
        document.NotStatedInGeneratedDocumentation.AddRange(omissions);

        // Absence in a generated artifact is "not represented here", never "the implementation has none"; requirement links are not expected.
        var gaps = document.EvidenceGaps.Where(g => g.Category != "Traceability").Select(g => g.State == DataModelEvidenceState.NotRepresented
            ? new DataModelEvidenceGap { Category = g.Category, EntityName = g.EntityName, State = g.State,
                Description = $"{g.Category} not represented in this generated artifact — this does not mean the implementation has none." }
            : g).ToList();
        var unstatedNullability = omissions.Count(o => o.Description.Contains("nullable/not-null", StringComparison.Ordinal));
        if (unstatedNullability > 0)
            gaps.Add(new DataModelEvidenceGap { Category = "Nullability", State = DataModelEvidenceState.NotRepresented,
                Description = $"{NotStated} for {unstatedNullability} field{(unstatedNullability == 1 ? "" : "s")} — required/optional is unknown, not a defect." });
        document.EvidenceGaps.Clear();
        document.EvidenceGaps.AddRange(gaps);
        return document;
    }

    /// <summary>Findings that only say the document does not state something.</summary>
    private static Func<DataModelFinding, bool> IsOmission(DataModelDocument document) => f =>
        f.Description.Contains("does not specify nullable/not-null", StringComparison.Ordinal)
        || f.Description.Contains("is missing a type", StringComparison.Ordinal)
        || f.Description.StartsWith("No primary key column identified", StringComparison.Ordinal)
        || f.Description.Contains("has no classification annotation", StringComparison.Ordinal)
        || document.Relationships.Count == 0 && f.Description.Contains("no relationship is defined for it", StringComparison.Ordinal);

    /// <summary>The nullability of a field as the document states it, with generated omissions as unknown.</summary>
    public static string NullabilityState(DataColumn column, bool generated) => column.Nullable switch
    {
        true => "Explicitly nullable",
        false => "Explicitly required (not null)",
        null when column.IsPrimaryKey => "Required (key)",
        null => generated ? NotStated : "Not stated",
    };
}
