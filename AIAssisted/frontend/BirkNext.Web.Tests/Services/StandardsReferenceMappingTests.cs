using BirkNext.Standards;
using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.BrowserCompanion;
using System.Text.Json;

namespace BirkNext.Web.Tests.Services;

public sealed class StandardsReferenceMappingTests
{
    [Fact]
    public void BrowserWcagMappingUsesCriterionMetadataAndRuntimeScope()
    {
        var reference = StandardsReferenceMappings.Wcag("1.4.3", "Contrast (Minimum)", StandardsEvidenceScope.BrowserRuntime);
        Assert.Equal("1.4.3", reference.ReferenceId);
        Assert.Equal(StandardsMappingType.Direct, reference.MappingType);
        Assert.Equal(StandardsEvidenceScope.BrowserRuntime, reference.EvidenceScope);
        Assert.Contains("1-4-3", reference.Url);
    }

    [Fact]
    public void AxeCriteriaComeOnlyFromTheExplicitRuleCatalog()
    {
        var mapped = AxeRuleCatalog.Criteria("color-contrast");
        Assert.Contains("1.4.3", mapped);
        Assert.Empty(AxeRuleCatalog.Criteria("unknown-axe-rule"));
    }

    [Fact]
    public void ApiMappingsAreExplicitAndUnknownRulesRemainUnmapped()
    {
        var mapped = StandardsReferenceMappings.ForApiRule("gql-error-leak");
        Assert.Single(mapped);
        Assert.Equal("A05:2021", mapped[0].ReferenceId);
        Assert.Equal(StandardsMappingType.Related, mapped[0].MappingType);
        Assert.Equal(StandardsEvidenceScope.ApiRuntime, mapped[0].EvidenceScope);
        Assert.Empty(StandardsReferenceMappings.ForApiRule("unknown-rule"));
        // Keys are emitted finding rule ids: "rest-slow" (the finding), never "rest-latency" (a check id).
        Assert.Single(StandardsReferenceMappings.ForApiRule("rest-slow"));
        Assert.Empty(StandardsReferenceMappings.ForApiRule("rest-latency"));
        Assert.Equal("A05:2021", Assert.Single(StandardsReferenceMappings.ForApiRule("sec-server-disclosure")).ReferenceId);
        Assert.Equal("A05:2021", Assert.Single(StandardsReferenceMappings.ForApiRule("cors-wildcard-credentials")).ReferenceId);
        Assert.Equal("A02:2021", Assert.Single(StandardsReferenceMappings.ForApiRule("sec-no-tls")).ReferenceId);
        Assert.Empty(StandardsReferenceMappings.ForApiRule("sec-server-exposed"));
    }

    [Fact]
    public void IntegrationMappingsRetainConfigurationVersusRuntimeScope()
    {
        // "sec-tls" is an IQR check id, never a finding rule id: no reference is attached to something that is never emitted.
        Assert.Empty(StandardsReferenceMappings.ForIntegrationRule("sec-tls"));
        Assert.Equal(StandardsEvidenceScope.IntegrationContract,
            Assert.Single(StandardsReferenceMappings.ForIntegrationRule("contract-incompatible")).EvidenceScope);
        Assert.Equal(StandardsEvidenceScope.IntegrationRuntime,
            Assert.Single(StandardsReferenceMappings.ForIntegrationRule("consumer-progress-stale")).EvidenceScope);
        Assert.Empty(StandardsReferenceMappings.ForIntegrationRule("unknown-rule"));
    }

    [Fact]
    public void ExistingFindingsDefaultToNoReferencesWithoutChangingTheirIdentityOrSeverity()
    {
        var api = new ApiReviewFinding { Id = "one", RuleId = "unknown", Severity = ApiReviewSeverity.High };
        var integration = new IntegrationReviewFinding { Key = "one", RuleId = "unknown", Severity = IntegrationFindingSeverityV2.High };
        Assert.Empty(api.StandardsReferences);
        Assert.Empty(integration.StandardsReferences);
        Assert.Equal("one", api.Id);
        Assert.Equal(ApiReviewSeverity.High, api.Severity);
        Assert.Equal("one", integration.Key);
        Assert.Equal(IntegrationFindingSeverityV2.High, integration.Severity);
    }

    [Fact]
    public void MultipleReferencesStayOnOneFindingAndLegacyJsonDefaultsToEmpty()
    {
        var finding = new ApiReviewFinding
        {
            Id = "one",
            Severity = ApiReviewSeverity.High,
            StandardsReferences =
            [
                StandardsReferenceMappings.ForApiRule("gql-error-leak")[0],
                StandardsReferenceMappings.ForApiRule("rest-slow")[0],
            ]
        };
        var report = new ApiReviewReport { Findings = [finding] };
        Assert.Single(report.Findings);
        Assert.Equal(2, report.Findings[0].StandardsReferences.Count);
        Assert.Equal(ApiReviewSeverity.High, report.Findings[0].Severity);

        var legacy = JsonSerializer.Deserialize<ApiReviewFinding>("{\"id\":\"old\"}");
        Assert.NotNull(legacy);
        Assert.Empty(legacy.StandardsReferences);
    }
}
