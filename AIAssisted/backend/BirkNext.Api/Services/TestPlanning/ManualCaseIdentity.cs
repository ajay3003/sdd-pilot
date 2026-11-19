using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The identity and origin of a saved manual test case. Used by the Azure DevOps Test Plans export (the case revision it names and the origin it
/// describes). Moved out of the retired manual run service on 2026-11-19: manual execution is owned by Azure DevOps Test Plans, the case's
/// identity is not.
/// </summary>
public static class ManualCaseIdentity
{
    /// <summary>The identity of a case's executable content: title, preconditions, steps and expected results as the tester sees them.</summary>
    public static string Fingerprint(TestCaseDraft c) => ManualRunRules.CaseFingerprint(c); // shared with the web app; same bytes as before

    /// <summary>Specification → scenario → case: the case's source, the scenario fingerprint, the specification file and acceptance scenario it was
    /// prepared from, and each requirement's file and basis — the same derivation Requirements Traceability uses.</summary>
    public static ManualRunOrigin Origin(TestCaseDraft c)
    {
        var fromSpecification = c.Source.Kind is not (ScenarioSourceKind.SystemGuide or ScenarioSourceKind.Manual) && c.Generated.Capability == TestScenarioDiscoveryEngine.Specification;
        var specFile = fromSpecification ? PreparedCaseTraceability.SpecificationFile(c.Generated.Evidence) : null;
        var sources = new List<ManualRunRequirementSource>();
        if (fromSpecification)
            foreach (var id in c.Generated.Requirements.Select(r => r.RuleId).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                sources.Add(new(id, PreparedCaseTraceability.RequirementFile(c.Generated.Evidence, id) ?? specFile, "Specification evidence"));
        foreach (var l in c.RequirementLinks.Where(l => !string.IsNullOrWhiteSpace(l.RequirementId)))
            sources.Add(new(l.RequirementId.Trim(), string.IsNullOrWhiteSpace(l.SpecificationPath) ? null : l.SpecificationPath.Trim(), "Manual link", string.IsNullOrWhiteSpace(l.LinkedBy) ? null : l.LinkedBy));
        return new ManualRunOrigin
        {
            SourceKind = c.Source.Kind, ScenarioFingerprint = c.Source.Fingerprint, SpecificationFile = specFile,
            SpecificationScenarioId = fromSpecification ? PreparedCaseTraceability.ScenarioIdOf(c.Generated.Evidence) : null,
            Requirements = [.. sources.DistinctBy(s => (s.RequirementId.ToUpperInvariant(), s.SpecificationFile, s.Basis))],
        };
    }
}
