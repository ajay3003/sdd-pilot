using BirkNext.RealProjectAcceptance;
using BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance;

/// <summary>
/// Every BirkNext page, in the order a user meets them in the sidebar, as one acceptance feature each. Dataset-agnostic: the same list
/// runs for any dataset; the dataset descriptor only adds expectations. A page added to the product needs an entry here (or an explicit
/// classification) — <see cref="ClassifiedElsewhere"/> lists pages that are deliberately not exercised and why.
/// </summary>
public static class BrowserAcceptanceFeatureRegistry
{
    /// <summary>Navigation entries that are optional/legacy by default and therefore not part of acceptance, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> ClassifiedElsewhere = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["traceability"] = "Legacy Traceability & Coverage: hidden unless the legacy navigation flag is enabled.",
        ["traceability/suggestions"] = "Legacy Traceability Suggestions: hidden unless the legacy navigation flag is enabled.",
        ["code-traceability"] = "Legacy Code Traceability: hidden unless the legacy navigation flag is enabled.",
        ["ai-change-auditor"] = "AI Change Review: optional feature, off by default; reviews an AI-proposed diff, not a project archive.",
        ["admin/system-settings"] = "System Settings: exercised through Target Environments, Security Configuration and the three diagnostics sections.",
    };

    public static IReadOnlyList<IRealProjectAcceptanceFeature> Create() =>
    [
        // Project Inputs first: the run's single import establishes the workspace every later feature is checked against.
        new ProjectImportFeature(),
        // Getting Started
        new RecommendedWorkflowFeature(),
        new UserGuideFeature(),
        new DashboardFeature(),
        // Project Inputs
        new SampleProjectsFeature(),
        new TargetEnvironmentsFeature(),
        // Document Review
        .. ExplorerFeature.All(),
        new DocumentQualityReviewFeature(),
        // Traceability
        new RequirementsTraceabilityFeature(),
        new ImplementationReviewFeature(),
        new ImplementationEvidenceReviewFeature(),
        new ImplementationTraceabilityFeature(),
        new RenderOnlyFeature("spec-drift", "Spec Drift", "Traceability", "/spec-drift", ".drift-page",
            note: "Spec Drift compares recorded requirement revisions; a first import has no earlier revision to drift from."),
        new RenderOnlyFeature("impact-analysis", "Impact Analysis", "Traceability", "/impact-analysis", ".ia-mode-selector",
            note: "Impact Analysis needs a proposed change as input; acceptance does not invent one."),
        // Source Review
        new SourceAnalysisFeature(),
        new TechnologyCoverageFeature(),
        new DependencyReviewFeature(),
        new PipelineReviewFeature(),
        new EnvironmentAnalysisFeature(),
        // Source evidence domains (shown inside Source Analysis, consumed by several reviews)
        new ContractEvidenceFeature("contract-openapi", "OpenAPI contracts", "OpenApi"),
        new ContractEvidenceFeature("contract-graphql", "GraphQL contracts", "GraphQlSchema", "GraphQlOperations"),
        new ContractEvidenceFeature("contract-xsd", "XML Schema contracts", "XmlSchema"),
        new InfrastructureEvidenceFeature(),
        new GeneratedDocumentationFeature(),
        // Quality & Testing + Security (target-gated: honest no-target state, source inputs verified, nothing executed)
        .. TargetGatedFeature.All(),
        // Source-based review profile in Quality & Testing (needs only the snapshot; runs deterministically).
        new AiGeneratedCodeReviewFeature(),
        // Diagnostics
        .. DiagnosticFeature.All(),
        // Cross-feature checks last: they read what the features above recorded.
        new RenderProvenanceFeature(),
        new NavigationPersistenceFeature(),
        new ResponsiveAccessibilityFeature(),
        new WorkspaceConsistencyFeature(),
    ];
}
