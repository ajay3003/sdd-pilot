using BirkNext.Applicability;
using BirkNext.Integrations;
using BirkNext.Technology;
using BirkNext.Web.Components;
using BirkNext.Web.Layout;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Project/technology independence in the UI: the M2LB template is only ever applied by an explicit action; navigation labels reviews by
/// applicability without hiding them; the coverage page shows unsupported technologies neutrally; the dashboard averages assessed areas only.
/// </summary>
public sealed class TechnologyIndependenceUiTests : BunitContext
{
    private sealed class FakeCoverageApi(ProjectTechnologyCoverage? coverage) : ITechnologyCoverageApiService
    {
        public Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(coverage);
    }

    private static ProjectTechnologyCoverage PaymentHub() => new()
    {
        EnvironmentId = "pay", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "PaymentHub.zip",
        Source = new SourceTechnologyCoverage
        {
            TotalFiles = 9, AnalyzedFiles = 4, UnsupportedSourceFiles = 3,
            Technologies =
            [
                new("lang.java", "Java", TechnologyArea.Language, BirkNext.Applicability.DetectionConfidence.Confirmed, 2, ["PaymentHub/src/main/java/example/payments/PaymentController.java"]),
                new("integration.kafka", "Apache Kafka", TechnologyArea.Integration, BirkNext.Applicability.DetectionConfidence.Inferred, 1, ["PaymentHub/src/main/resources/application.yml"]),
                new("dependency.maven", "Maven / Gradle", TechnologyArea.Dependency, BirkNext.Applicability.DetectionConfidence.Confirmed, 1, ["PaymentHub/pom.xml"]),
                new("pipeline.github-actions", "GitHub Actions", TechnologyArea.Pipeline, BirkNext.Applicability.DetectionConfidence.Confirmed, 1, ["PaymentHub/.github/workflows/ci.yml"]),
                new("cloud.aws", "Amazon Web Services", TechnologyArea.Cloud, BirkNext.Applicability.DetectionConfidence.Confirmed, 1, ["PaymentHub/infra/main.tf"]),
            ],
            Capabilities = [new(Capability.SourceCode, BirkNext.Applicability.DetectionConfidence.Confirmed, "Source files"), new(Capability.EventStreaming, BirkNext.Applicability.DetectionConfidence.Inferred, "Kafka")],
        },
    };

    private void Register(ProjectTechnologyCoverage? coverage, string? targetUrl = null)
    {
        Services.AddSingleton<FeatureVisibilityService>();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "pay", Name = "PaymentHub", TargetUrl = targetUrl } });
        Services.AddSingleton(context.Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton<ITechnologyCoverageApiService>(new FakeCoverageApi(coverage));
        Services.AddScoped<ProjectApplicabilityState>();
    }

    [Fact]
    public void Navigation_LabelsReviewsByApplicability_WithoutHidingThem()
    {
        Register(PaymentHub());
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=nav-applicability-azure-environment]").TextContent.Should().Be("Unsupported"));
        cut.Find("[data-testid=nav-applicability-pipeline-review]").TextContent.Should().Be("Partial");
        cut.Find("[data-testid=nav-applicability-dependency-review]").TextContent.Should().Be("Unsupported");
        cut.Find("[data-testid=nav-applicability-frontend-quality-review]").TextContent.Should().Be("N/A", "no browser frontend in the analyzed source and no target");
        cut.Find("[data-testid=nav-applicability-security-classification-review]").TextContent.Should().Be("N/A");
        cut.FindAll("a[href='azure-environment']").Should().ContainSingle("unsupported reviews stay reachable, with an explanation");
        cut.FindAll("a[href='technology-coverage']").Should().ContainSingle();
        // Not enabled for this project: the generic menu names neither M2LB nor an extension, only the neutral N/A.
        cut.Find("a[href='security-classification-review']").TextContent.Should().NotContain("M2LB").And.NotContain("Extension");
        cut.Find("a[href='azure-environment'] .nav-label").TextContent.Should().Be("Environment Analysis", "the menu does not present Azure as universal");
    }

    [Fact]
    public void Navigation_WithoutTheStateService_RendersAsBefore()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        var cut = Render<NavMenu>();
        cut.FindAll(".nav-applicability").Should().BeEmpty();
        cut.FindAll("a[href='pipeline-review']").Should().ContainSingle();
    }

    [Fact]
    public void CoveragePage_ShowsUnsupportedTechnologiesNeutrally_AndPerDimension()
    {
        Register(PaymentHub());
        var cut = Render<TechnologyCoverage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tc-tech-integration\\.kafka]"));
        cut.Find("[data-testid=tc-unsupported-files]").TextContent.Should().Be("3");
        var kafka = cut.Find("[data-testid=tc-tech-integration\\.kafka]");
        kafka.GetAttribute("data-overall").Should().Be("Partial");
        kafka.TextContent.Should().Contain("Inferred").And.Contain("No broker");
        kafka.QuerySelectorAll(".sd-pill").Select(p => p.TextContent).Should().Contain(["Partial", "Not supported"]);
        cut.Find("[data-testid=tc-tech-lang\\.java]").GetAttribute("data-overall").Should().Be("Unsupported");
        cut.FindAll(".sd-pill-attention").Should().BeEmpty("unsupported is a tool limitation, never the failure tone");
        cut.Find("[data-testid=tc-review-source-analysis]").GetAttribute("data-status").Should().Be("Unsupported");
        cut.Find("[data-testid=tc-review-dependency-review]").TextContent.Should().Contain("SBOM");
        cut.Find("[data-testid=tc-extensions]").TextContent.Should().Be("None (generic project)");
    }

    [Fact]
    public void CoveragePage_DocumentOnlyProject_SaysWhatIsMissing()
    {
        Register(new ProjectTechnologyCoverage { EnvironmentId = "doc", Notices = ["No source archive has been analyzed for this environment."] });
        var cut = Render<TechnologyCoverage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tc-no-technologies]"));
        cut.Find("[data-testid=tc-notice]").TextContent.Should().Contain("No source archive");
        cut.Find("[data-testid=tc-review-source-analysis]").GetAttribute("data-status").Should().Be("NotEnoughEvidence");
        cut.Find("[data-testid=tc-review-pipeline-review]").GetAttribute("data-status").Should().Be("NotEnoughEvidence");
    }

    [Fact]
    public void IntegrationsPane_SuggestsTheM2lbTemplate_AndAppliesItOnlyOnClick()
    {
        var api = new FakeIntegrationCatalogApi
        {
            Catalog = new IntegrationCatalog { Templates = [new("m2lb-dev-eventhub", "M2LB DEV integration template", "Project-specific.", true, "The target is the M2LB DEV frontend (m2lbdev.bufetat.no).")] },
            AfterTemplate = new IntegrationCatalog { AppliedTemplateId = "m2lb-dev-eventhub", DomainExtensions = [DomainExtensionIds.M2lbChildSecurityClassification] },
        };
        Services.AddSingleton<IIntegrationCatalogApiService>(api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        var cut = Render<IntegrationsPane>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" }));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-template-suggestion]").TextContent.Should().Contain("not applied automatically"));
        api.Calls.Should().NotContain(c => c.StartsWith("apply-template"), "loading never applies a template");

        cut.Find("[data-testid=ip-template-apply]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-template-applied]").TextContent.Should().Contain("M2LB DEV integration template"));
        api.Calls.Should().Contain("apply-template:m2lb-dev-eventhub");
        cut.FindAll("[data-testid=ip-template-suggestion]").Should().BeEmpty();
    }

    [Fact]
    public void GenericProject_GetsNoSuggestion_OnlyACollapsedTemplateList()
    {
        var api = new FakeIntegrationCatalogApi { Catalog = new IntegrationCatalog { Templates = [new("m2lb-dev-eventhub", "M2LB DEV integration template", "Project-specific.", false)] } };
        Services.AddSingleton<IIntegrationCatalogApiService>(api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        var cut = Render<IntegrationsPane>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "pay", Name = "PaymentHub", TargetUrl = "https://paymenthub.example.test/" }));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-templates]"));
        cut.FindAll("[data-testid=ip-template-suggestion]").Should().BeEmpty();
        cut.Find("[data-testid=ip-templates]").HasAttribute("open").Should().BeFalse();
    }

    [Fact]
    public void Dashboard_AveragesAssessedAreasOnly_InsufficientQaIsNotZero()
    {
        // Traceability 90, compliance 70, QA report without a specification (score 0 = insufficient data), the rest not run.
        var qaAssessed = DashboardQualityAggregate.QaAssessed(hasData: true, sessionResult: false, workspaceHasSpecification: false, auditHasSpecification: false, readinessHasSpecification: null);
        qaAssessed.Should().BeFalse();
        var result = DashboardQualityAggregate.Compute([(true, 90), (true, 70), (qaAssessed, 0), (false, 0), (false, 0), (false, 0)]);
        result.Quality.Should().Be(80, "the insufficient-data QA 0 must not pull the average down to 53");
        result.CoverageLabel.Should().Be("2 of 6 areas assessed");
        DashboardQualityAggregate.Compute([(false, 0), (false, 0)]).Quality.Should().BeNull("nothing assessed is no score, not 0");
        DashboardQualityAggregate.QaAssessed(true, false, false, true, null).Should().BeTrue();
    }

    [Fact]
    public void NeutralStates_UseNeutralTones()
    {
        TechnologyCoveragePresentation.Tone(ApplicabilityStatus.Unsupported).Should().Be("muted");
        TechnologyCoveragePresentation.Tone(ApplicabilityStatus.NotApplicable).Should().Be("muted");
        TechnologyCoveragePresentation.Tone(SupportLevel.Unsupported).Should().Be("muted");
        TechnologyCoveragePresentation.Tone(SupportLevel.Planned).Should().Be("muted");
        TechnologyCoveragePresentation.NavBadge(new ReviewApplicability { Status = ApplicabilityStatus.Applicable }).Should().BeNull();
    }
}
