using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// SCIM identity provisioning presentation: the Entra → SCIM → KjentBruker → Service Bus → Autorisasjon flow with source and runtime evidence
/// per node, pre-run availability (base URL unknown, Production refused, mutation off), the safe-check result with deactivation shown
/// prominently, the specification comparison, settings validation, export, and the IQR pre-run/result sections.
/// </summary>
public sealed class ScimProvisioningUiTests : BunitContext
{
    private const string PlatformId = "dev:scim:m2lb";
    private readonly FakeIntegrationCatalogApi _api = new() { Readiness = M2lbFixture.Readiness(), Result = M2lbFixture.Result() };

    private static IntegrationPlatform Platform(string? baseUrl = null) => new()
    {
        Id = PlatformId, EnvironmentId = "dev", Name = "M2LB Entra SCIM Provisioning", Kind = IntegrationKind.IdentityProvisioning,
        ScimProvisioning = new ScimProvisioningSettings
        {
            BaseUrl = baseUrl, Authentication = "Entra ID JWT (Microsoft.Azure.SyncFabric)", Persistence = "KjentBruker", OutboundPlatformId = "dev:servicebus:m2lb",
            ConfigurationNotes = ["ServiceBus__FQDN is set to the platform Service Bus namespace FQDN."],
        },
    };

    private static ScimSourceEvidence Source() => new()
    {
        EnvironmentId = "dev", AnalyzedAt = DateTimeOffset.UtcNow, Detected = true, Project = "M2LB.Autorisasjon.ScimAdapter", BasePath = "/scim/v2",
        Archives = [new SourceArchive("M2LB (1).zip", new string('a', 64), 400)],
        Operations = [new() { Method = "POST", Path = "/scim/v2/Users" }, new() { Method = "PATCH", Path = "/scim/v2/Users/{id:guid}" }],
        Facts =
        [
            new() { Id = "scim-auth-required", Area = ScimArea.Security, Title = "Authorization required", State = ScimEvidenceState.SourceVerified },
            new() { Id = "scim-persistence", Area = ScimArea.Configuration, Title = "KjentBruker persistence", State = ScimEvidenceState.SourceVerified },
        ],
        Events = [new() { EventType = "BrukerDeaktivert", Topic = "entra.brukere" }],
    };

    private static ScimEvidenceCheck Check() => new()
    {
        RunId = Guid.NewGuid(), EnvironmentId = "dev", PlatformId = PlatformId, PlatformName = "M2LB Entra SCIM Provisioning", CompletedAt = DateTimeOffset.UtcNow,
        OverallState = ScimOverallState.IssueDetected, Settings = Platform().ScimProvisioning!, SourceDetected = true,
        Stages = Enum.GetValues<ScimStage>().Select(s => new ScimStageEvidence
        {
            Stage = s, Title = ScimLabels.Stage(s),
            Source = s switch { ScimStage.EntraProvisioning => ScimEvidenceState.NotAssessed, ScimStage.DownstreamProcessing or ScimStage.AuthorizationState => ScimEvidenceState.NotFound, ScimStage.ServiceBusRoute => ScimEvidenceState.Matched, _ => ScimEvidenceState.SourceVerified },
            SourceDetail = $"{s} source detail",
            Runtime = s == ScimStage.KjentBrukerPersistence ? ScimEvidenceState.NotTested : ScimEvidenceState.NotConfigured, RuntimeDetail = $"{s} runtime detail",
        }).ToList(),
        Checks =
        [
            new() { CheckId = "scim-auth-required", Area = ScimArea.Security, Title = "Authorization required on the SCIM group", State = ScimEvidenceState.SourceVerified, Provenance = IntegrationEvidenceSource.SourceCode, Locations = [new("Autorisasjon/src/Scim/Endpoints/UsersEndpoints.cs", 14)] },
            new() { CheckId = "scim-deactivation-propagation", Area = ScimArea.Security, Title = "Deactivation propagation (security-critical)", State = ScimEvidenceState.IssueDetected, Detail = "No consumer of the topic is in the analyzed source. A published deactivation is not revoked access.", Provenance = IntegrationEvidenceSource.SourceCode },
            new() { CheckId = "scim-retry-policy", Area = ScimArea.Reliability, Title = "Publish retry policy", State = ScimEvidenceState.SourceVerified, Provenance = IntegrationEvidenceSource.SourceCode },
            new() { CheckId = "scim-retry-observed", Area = ScimArea.Reliability, Title = "Retry observed", State = ScimEvidenceState.NotTested, Provenance = IntegrationEvidenceSource.Configuration },
            new() { CheckId = "scim-config-base-url", Area = ScimArea.Configuration, Title = "SCIM base URL", State = ScimEvidenceState.NotConfigured, Provenance = IntegrationEvidenceSource.Configuration },
        ],
        Requirements =
        [
            new() { Id = "FR-003", Text = "Authenticate using a Bearer token (provisioning secret).", Status = ScimRequirementStatus.PartiallyImplemented, Evidence = "Entra ID JWT, not a provisioning secret." },
            new() { Id = "FR-021", Text = "Health endpoint with SQL and Service Bus connectivity.", Status = ScimRequirementStatus.NotFound, Evidence = "No check registered." },
        ],
        TestCoverage = [new() { Scenario = "Invalid token rejected (401, no event)", State = ScimTestCoverageState.TestedWithFake, Tests = ["ScimUsersEndpointTests.PostUser_WrongBearerToken_Returns401_NoEvent"], Note = "production appid/oid policy is not exercised" }],
        Findings = [new() { RuleId = "scim-deactivation-not-established", Severity = ScimFindingSeverity.High, Area = ScimArea.Security, Title = "Deactivation reaching effective access is not established", Detail = "No consumer.", Recommendation = "Identify the consumer." }],
        Runtime = new ScimRuntimeEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No SCIM base URL is configured." },
        SyntheticMutation = new ScimMutationCapability { State = ScimMutationState.NotConfigured, Reasons = ["Requires an explicitly enabled synthetic test context."] },
        Missing = ["The public SCIM base URL (tenant URL) — needed for the safe endpoint, health and authentication checks.", "An approved synthetic SCIM test context."],
    };

    private readonly List<ScimProvisioningSettings> _saved = [];

    public ScimProvisioningUiTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ScimProvisioningPanel> Panel(FrontendEnvironmentType environment = FrontendEnvironmentType.Development, string? baseUrl = null) =>
        Render<ScimProvisioningPanel>(p => p
            .Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = environment })
            .Add(c => c.Platform, Platform(baseUrl))
            .Add(c => c.SaveSettings, s => { _saved.Add(s); return Task.FromResult(true); }));

    [Fact]
    public void FlowShowsFiveNodesWithSourceAndRuntimeSeparately()
    {
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() };

        var cut = Panel();

        var nodes = cut.FindAll("[data-testid='scim-node']");
        nodes.Should().HaveCount(5);
        nodes[0].TextContent.Should().Contain("Microsoft Entra ID").And.Contain("Not assessed");
        nodes[1].TextContent.Should().Contain("SCIM adapter").And.Contain("/scim/v2").And.Contain("Source verified").And.Contain("Runtime: Not assessed");
        nodes[3].TextContent.Should().Contain("entra.brukere");
        cut.Find("[data-testid='scim-flow']").GetAttribute("aria-label").Should().Be("Provisioning flow");
    }

    [Fact]
    public void PreRunShowsConfiguredVsSourceAndLimitedRuntime()
    {
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() };

        var cut = Panel();

        cut.Find("[data-testid='scim-row-scim']").TextContent.Should().Contain("Confirmed from source");
        cut.Find("[data-testid='scim-row-auth']").TextContent.Should().Contain("Source verified");
        cut.Find("[data-testid='scim-row-runtime']").TextContent.Should().Contain("Limited — base URL unknown");
        cut.Find("[data-testid='scim-row-mutation']").TextContent.Should().Contain("Not configured");
        cut.Find("[data-testid='scim-source-provenance']").TextContent.Should().Contain("M2LB (1).zip").And.Contain("M2LB.Autorisasjon.ScimAdapter");
        cut.Find("[data-testid='scim-empty']").TextContent.Should().Contain("No SCIM check yet");
    }

    [Fact]
    public void ProductionIsNeverOfferedAsASafeCheckTarget()
    {
        var cut = Panel(FrontendEnvironmentType.Production, "https://scim.example.test");

        cut.Find("[data-testid='scim-row-runtime']").TextContent.Should().Contain("Not allowed (Production)");
        cut.Find("[data-testid='scim-safe-detail']").TextContent.Should().Contain("Production is never contacted");
    }

    [Fact]
    public void WithoutSourceTheScimRowSaysConfiguredNotConfirmed()
    {
        var cut = Panel();

        cut.Find("[data-testid='scim-row-scim']").TextContent.Should().Contain("Configured — source not analyzed");
        cut.Find("[data-testid='scim-row-endpoint']").TextContent.Should().Contain("Configured");
    }

    [Fact]
    public void RunSafeChecksShowsResultWithDeactivationProminentAndNothingPassedFromSource()
    {
        _api.ScimCheck = _ => Check();
        var cut = Panel();

        cut.Find("[data-testid='scim-run']").Click();

        _api.Calls.Should().Contain("scim-checks:" + PlatformId);
        _api.ScimCheckEnvironmentTypes.Should().Equal("Development");
        cut.Find("[data-testid='scim-overall']").TextContent.Should().Be("Issue detected");
        cut.Find("[data-testid='scim-deactivation']").TextContent.Should().Contain("security-critical").And.Contain("Issue detected").And.Contain("not revoked access");
        cut.Find("[data-testid='scim-missing']").TextContent.Should().Contain("public SCIM base URL").And.Contain("synthetic SCIM test context");
        cut.Find("[data-testid='scim-status']").TextContent.Should().Contain("Checked.");
        cut.Find("[data-testid='scim-result']").TextContent.Should().NotContain("Passed");
        var security = cut.Find("[data-testid='scim-highlight'][data-area='Security']");
        security.TextContent.Should().Contain("Deactivation propagation").And.Contain("Not established:");
        cut.Find("[data-testid='scim-highlight'][data-area='Reliability']").TextContent.Should().Contain("Retry observed — Not tested");
    }

    [Fact]
    public void StageTableKeepsSourceAndRuntimeColumns()
    {
        _api.ScimCheck = _ => Check();
        var cut = Panel();
        cut.Find("[data-testid='scim-run']").Click();

        var row = cut.Find("[data-testid='scim-stage-table'] tr[data-stage='KjentBrukerPersistence']");
        row.QuerySelectorAll("td")[0].TextContent.Should().Contain("Source verified");
        row.QuerySelectorAll("td")[1].TextContent.Should().Contain("Not tested");
        cut.Find("[data-testid='scim-stage-table'] tr[data-stage='ServiceBusRoute'] td").TextContent.Should().Contain("Matched");
        cut.Find("[data-testid='scim-stages'] button").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void SpecificationComparisonAndRepositoryTestsAreShown()
    {
        _api.ScimCheck = _ => Check();
        var cut = Panel();
        cut.Find("[data-testid='scim-run']").Click();

        cut.Find("[data-testid='scim-requirement-table'] tr[data-requirement='FR-003']").TextContent.Should().Contain("Partially implemented").And.Contain("not a provisioning secret");
        cut.Find("[data-testid='scim-requirement-table'] tr[data-requirement='FR-021']").TextContent.Should().Contain("Not found");
        cut.Find("[data-testid='scim-requirements'] button").TextContent.Should().Contain("1 partially implemented").And.Contain("1 not found");
        cut.Find("[data-testid='scim-test-coverage']").TextContent.Should().Contain("Tested with a fake").And.Contain("not exercised");
        cut.Find("[data-testid='scim-mutation']").TextContent.Should().Contain("Not configured");
    }

    [Fact]
    public void CheckErrorIsShownAndNoResultAppears()
    {
        _api.ScimCheckError = "Platform not found.";
        var cut = Panel();

        cut.Find("[data-testid='scim-run']").Click();

        cut.Find("[data-testid='scim-error']").TextContent.Should().Be("Platform not found.");
        cut.FindAll("[data-testid='scim-result']").Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeUploadsArchivesAndReportsDetection()
    {
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() };
        var cut = Panel();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "M2LB (1).zip"), InputFileContent.CreateFromBinary([4], "M2LB.Common.zip"));

        _api.Analyzed.Should().Equal("M2LB (1).zip", "M2LB.Common.zip");
        cut.Find("[data-testid='scim-status']").TextContent.Should().Contain("SCIM detected in M2LB.Autorisasjon.ScimAdapter");
    }

    [Fact]
    public void SettingsRejectACredentialBearingUrlAndSaveAValidOne()
    {
        var cut = Panel();
        cut.Find("[data-testid='scim-configure']").Click();

        cut.Find("[data-testid='scim-base-url']").Change("https://scim.example.test/?code=secret");
        cut.Find("[data-testid='scim-save']").Click();
        cut.Find("[data-testid='scim-form-error']").TextContent.Should().Contain("query string");
        _saved.Should().BeEmpty();

        cut.Find("[data-testid='scim-base-url']").Change("https://scim.example.test/");
        cut.Find("[data-testid='scim-synthetic-environment']").Change("QA");
        cut.Find("[data-testid='scim-synthetic-prefix']").Change("ola.nordmann");
        cut.Find("[data-testid='scim-save']").Click();
        cut.Find("[data-testid='scim-form-error']").TextContent.Should().Contain(ScimSyntheticTestContext.RequiredPrefix);

        cut.Find("[data-testid='scim-synthetic-prefix']").Change(ScimSyntheticTestContext.RequiredPrefix + "1");
        cut.Find("[data-testid='scim-save']").Click();
        _saved.Should().ContainSingle().Which.BaseUrl.Should().Be("https://scim.example.test");
        _saved[0].SyntheticTest.Enabled.Should().BeFalse();
        cut.FindAll("[data-testid='scim-form']").Should().BeEmpty();
        cut.FindAll("[data-testid='scim-synthetic-environment'] option").Should().BeEmpty();
    }

    [Fact]
    public void SyntheticEnvironmentOptionsAreDevAndQaOnly()
    {
        var cut = Panel();
        cut.Find("[data-testid='scim-configure']").Click();

        cut.FindAll("[data-testid='scim-synthetic-environment'] option").Select(o => o.GetAttribute("value")).Should().Equal("", "DEV", "QA");
    }

    [Fact]
    public void ExportDownloadsHtmlWithoutSecrets()
    {
        _api.ScimCheck = _ => Check();
        var cut = Panel();
        cut.Find("[data-testid='scim-run']").Click();

        cut.Find("[data-testid='scim-export']").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
        var html = new ReportExportService().ExportScimCheck(Check());
        html.Should().Contain("Evidence by stage").And.Contain("Specification vs implementation").And.Contain("Deactivation reaching effective access").And.Contain("Missing evidence");
        html.Should().NotContainAny("Authorization: Bearer", "Bearer ey", "birknext-invalid-token", "SharedAccessKey");
    }

    [Fact]
    public void IntegrationsPaneRendersScimPanelWithoutEventHubRuntimeFields()
    {
        _api.Catalog = M2lbFixture.Catalog() with { Platforms = [M2lbFixture.Platform(), Platform()] };
        var cut = Render<IntegrationsPane>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development }));

        cut.WaitForElement("[data-testid='scim']");
        var section = cut.FindAll("[data-testid='ip-platform']").Single(s => s.TextContent.Contains("M2LB Entra SCIM Provisioning"));
        section.TextContent.Should().Contain("Identity provisioning");
        section.QuerySelector("[data-testid='ip-platform-namespace']").Should().BeNull();
        section.QuerySelector("[data-testid='ip-runtime-summary']").Should().BeNull("Event Hub runtime evidence sources do not apply to identity provisioning");
    }

    [Fact]
    public void IqrShowsScimReadinessAndResultSnapshot()
    {
        _api.Readiness = M2lbFixture.Readiness() with
        {
            Scim = [new ScimReadiness { PlatformId = PlatformId, PlatformName = "M2LB Entra SCIM Provisioning", SourceAnalyzed = true, Detected = true, Operations = 5, RequirementsImplemented = 9, RequirementsTotal = 25, NeedsReview = 17, RuntimeState = IntegrationEvidenceState.NotConfigured, RuntimeReason = "No SCIM base URL is configured." }],
        };
        _api.Result = M2lbFixture.Result() with { ScimSnapshot = [Check()] };
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } });
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<RuntimeReviewSessionService>();

        var cut = Render<IntegrationQualityReview>();

        cut.WaitForElement("[data-testid='iqr-scim']");
        cut.Find("[data-testid='iqr-scim-source']").TextContent.Should().Be("Source verified");
        cut.Find("[data-testid='iqr-scim-runtime']").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid='iqr-scim-mutation']").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid='iqr-scim']").TextContent.Should().Contain("9 of 25 specification requirement(s) implemented");
        cut.Find("[data-testid='iqr-run']").Click();
        cut.WaitForElement("[data-testid='iqr-result-scim']");
        cut.Find("[data-testid='iqr-result-scim-platform']").TextContent.Should().Contain("Issue detected").And.Contain("KjentBruker persistence: Source verified / runtime Not tested");
    }

    [Fact]
    public void PresentationRules()
    {
        ScimPresentation.Worst([ScimEvidenceState.Observed, ScimEvidenceState.NotTested]).Should().Be(ScimEvidenceState.NotTested);
        ScimPresentation.Worst([ScimEvidenceState.SourceVerified, ScimEvidenceState.IssueDetected]).Should().Be(ScimEvidenceState.IssueDetected);
        ScimPresentation.NodeState(Check(), [ScimStage.ScimEndpoint, ScimStage.Authentication]).Source.Should().Be(ScimEvidenceState.SourceVerified);
        ScimPresentation.SafeChecks(new ScimProvisioningSettings { BaseUrl = "https://x" }, new FrontendEnvironmentTypeName("Custom")).State.Should().Be(ScimEvidenceState.NotSupported);
        ScimPresentation.SafeChecks(new ScimProvisioningSettings { BaseUrl = "https://x" }, new FrontendEnvironmentTypeName("QA")).Label.Should().Be("Available");
        ScimPresentation.MutationState(new ScimProvisioningSettings { SyntheticTest = new() { Environment = "QA", TestUserPrefix = ScimSyntheticTestContext.RequiredPrefix, Enabled = true, ApprovedByTestLead = true, CleanupPlan = "x" } }).Should().Be(ScimMutationState.NotImplemented);
        ScimPresentation.Tone(ScimEvidenceState.SourceVerified).Should().NotBe(ScimPresentation.Tone(ScimEvidenceState.Verified));
        ScimPresentation.Location(new SourceLocation("Autorisasjon/src/M2LB.Autorisasjon.ScimAdapter/Services/ScimUserService.cs", 96)).Should().Be("Services/ScimUserService.cs:96");
    }
}
