using BirkNext.CriticalE2E;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestCoverage;

/// <summary>
/// Test Data identity for Test Coverage QA tests, read only from authoritative links (never from names or aliases alone):
/// <list type="bullet">
/// <item>A Critical E2E flow definition's own stable asset ids (<see cref="CriticalE2EFlowDefinition.TestDataAssetIds"/> and the asset ids of its
/// <see cref="CriticalE2EFlowDefinition.DataMappings"/>). An alias-only (legacy) reference states a data need without a stable id.</item>
/// <item>A Test Plan draft case linked to the flow — its source is the flow, or a reviewer Confirmed its correlation to the flow — with its
/// <see cref="TestCaseDataLink.AssetId"/>s. A case that describes required test data but links no asset states a need without a link.</item>
/// </list>
/// Nothing found is <see cref="CoverageDataNeed.NotAssessed"/>: absence never means "no data needed".
/// </summary>
public static class TestCoverageDataLinks
{
    public const string FlowSource = "Critical E2E flow definition";

    public static E2EFlowEvidence With(E2EFlowEvidence flow, CriticalE2EFlowDefinition? definition, IEnumerable<TestPlanDraft> drafts, string? environmentId)
    {
        var links = new List<CoverageTestDataLink>();
        var needs = new List<string>();
        if (definition is not null)
        {
            var aliases = definition.TestDataAliases ?? [];
            var ids = definition.TestDataAssetIds ?? [];
            for (var i = 0; i < Math.Max(aliases.Count, ids.Count); i++)
            {
                var alias = i < aliases.Count ? aliases[i] : null;
                var id = i < ids.Count ? ids[i] : null;
                if (!string.IsNullOrWhiteSpace(id)) links.Add(new CoverageTestDataLink(id!, alias, null, FlowSource));
                else if (!string.IsNullOrWhiteSpace(alias)) needs.Add($"The flow references Test Data alias \"{alias}\" without a stable asset id.");
            }
            foreach (var mapping in definition.DataMappings ?? [])
                for (var i = 0; i < Math.Max(mapping.AssetAliases.Count, mapping.AssetIds?.Count ?? 0); i++)
                {
                    var alias = i < mapping.AssetAliases.Count ? mapping.AssetAliases[i] : null;
                    var id = mapping.AssetIds is { } mids && i < mids.Count ? mids[i] : null;
                    if (!string.IsNullOrWhiteSpace(id)) links.Add(new CoverageTestDataLink(id!, alias, null, $"{FlowSource} (data mapping {mapping.Variable})"));
                    else if (!string.IsNullOrWhiteSpace(alias)) needs.Add($"Data mapping {mapping.Variable} references Test Data alias \"{alias}\" without a stable asset id.");
                }
        }
        foreach (var plan in drafts)
            foreach (var testCase in plan.Cases.Where(c => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded) && LinksFlow(c, flow.Id, environmentId)))
            {
                var source = $"Test Plan draft \"{plan.PlanName}\" (revision {plan.Revision}) — {testCase.EffectiveTitle}";
                foreach (var link in testCase.TestData.Where(l => !string.IsNullOrWhiteSpace(l.AssetId)))
                    links.Add(new CoverageTestDataLink(link.AssetId, string.IsNullOrWhiteSpace(link.Alias) ? null : link.Alias, link.Fingerprint, source));
                if (testCase.TestData.Count == 0 && !string.IsNullOrWhiteSpace(testCase.Generated.RequiredTestData))
                    needs.Add($"{source} describes required test data but links no Test Data asset.");
            }
        var distinct = links.DistinctBy(l => (l.AssetId, l.Source)).ToList();
        if (distinct.Count > 0)
            return flow with { TestData = distinct, DataNeed = CoverageDataNeed.Linked, DataNeedBasis = needs.Count == 0 ? null : string.Join(" ", needs.Distinct(StringComparer.Ordinal)) };
        return needs.Count > 0
            ? flow with { TestData = [], DataNeed = CoverageDataNeed.RequiredButMissing, DataNeedBasis = string.Join(" ", needs.Distinct(StringComparer.Ordinal)) }
            : flow with { TestData = [], DataNeed = CoverageDataNeed.NotAssessed, DataNeedBasis = null };
    }

    /// <summary>The case is about this flow: its source is the flow, or a reviewer confirmed its correlation to the flow (same environment when known).</summary>
    private static bool LinksFlow(TestCaseDraft c, string flowId, string? environmentId)
    {
        bool Same(ScenarioReference r) => r.Kind == ScenarioSourceKind.CriticalE2EFlow && string.Equals(r.ScenarioId, flowId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(r.Owner) || string.Equals(r.Owner, environmentId, StringComparison.Ordinal));
        return Same(c.Source) || c.Correlations.Any(x => x.State == CorrelationState.Confirmed && Same(x.Target));
    }
}
