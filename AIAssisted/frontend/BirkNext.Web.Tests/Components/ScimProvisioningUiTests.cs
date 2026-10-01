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

    private static AngleSharp.Dom.IElement Node(IRenderedComponent<ScimProvisioningPanel> cut, ScimStage stage) => cut.Find($"[data-testid='scim-node'][data-stage='{stage}']");

    private static (string Configuration, string Source, string Runtime) Dimensions(AngleSharp.Dom.IElement node) => (
        node.QuerySelector("[data-testid='scim-node-configuration'] .scim-badge")!.TextContent,
        node.QuerySelector("[data-testid='scim-node-source'] .scim-badge")!.TextContent,
        node.QuerySelector("[data-testid='scim-node-runtime'] .scim-badge")!.TextContent);

    private IRenderedComponent<ScimProvisioningPanel> PanelWith(ScimSyntheticTestContext synthetic) =>
        Render<ScimProvisioningPanel>(p => p
            .Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development })
            .Add(c => c.Platform, Platform() with { ScimProvisioning = Platform().ScimProvisioning! with { SyntheticTest = synthetic } }));

    [Fact]
    public void ConfiguredWithoutSourceOrRunShowsThreeSeparateDimensionsPerNode()
    {
        var cut = Panel();

        var nodes = cut.FindAll("[data-testid='scim-node']");
        nodes.Should().HaveCount(5);
        cut.Find("[data-testid='scim-flow']").GetAttribute("aria-label").Should().Be("Provisioning flow");
        nodes.Select(n => n.QuerySelector(".scim-node-title")!.TextContent).Should().Equal("Microsoft Entra ID", "SCIM adapter", "KjentBruker", "Service Bus", "Autorisasjon");
        Dimensions(Node(cut, ScimStage.EntraProvisioning)).Should().Be(("Not assessed", "Not analyzed", "Not assessed"));
        Dimensions(Node(cut, ScimStage.ScimEndpoint)).Should().Be(("Configured", "Not analyzed", "Not assessed"));
        Node(cut, ScimStage.ScimEndpoint).TextContent.Should().Contain("/scim/v2");
        Dimensions(Node(cut, ScimStage.KjentBrukerPersistence)).Should().Be(("Configured", "Not analyzed", "Not assessed"));
        Dimensions(Node(cut, ScimStage.ServiceBusPublish)).Should().Be(("Configured", "Not analyzed", "Not assessed"));
        Node(cut, ScimStage.ServiceBusPublish).TextContent.Should().Contain("entra.brukere");
        Dimensions(Node(cut, ScimStage.DownstreamProcessing)).Should().Be(("Not assessed", "Not analyzed", "Not assessed"));
        foreach (var node in nodes)
            node.QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Configuration", "Source evidence", "Runtime evidence");
        cut.Markup.Should().NotContain("Runtime: ", "a node never carries an unlabeled runtime badge next to a configuration badge");
    }

    [Fact]
    public void ConfigurationSectionListsSettingsOnlyAndNeverImpliesRuntime()
    {
        var cut = Panel();
        var configuration = cut.Find("[data-testid='scim-configuration']");
        configuration.QuerySelectorAll("dl.scim-rows > div[data-testid]").Select(r => r.GetAttribute("data-testid")).Should().Equal(
            "scim-row-endpoint", "scim-row-auth", "scim-row-persistence", "scim-row-outbound", "scim-row-events");
        cut.Find("[data-testid='scim-row-endpoint']").TextContent.Should().Contain("Configured").And.Contain("/scim/v2");
        cut.Find("[data-testid='scim-row-auth']").TextContent.Should().Contain("Entra ID JWT").And.NotContain("SyncFabric", "the technical qualifier is in the collapsed details");
        cut.Find("[data-testid='scim-row-persistence']").TextContent.Should().Contain("KjentBruker");
        cut.Find("[data-testid='scim-row-outbound']").TextContent.Should().Contain("Service Bus entra.brukere");
        cut.Find("[data-testid='scim-row-events']").TextContent.Should().Contain("BrukerAktivert, BrukerDeaktivert");
        configuration.QuerySelectorAll("dl.scim-rows > div[data-testid] .scim-badge").Select(b => b.TextContent).Should().OnlyContain(t => t == "Configured" || t == "Not configured");
        configuration.TextContent.Should().NotContainAny("Runtime", "Observed", "Reachable", "Published", "Verified", "Limited");
        configuration.TextContent.Should().Contain("configured is not reachable, verified or observed");
    }

    [Fact]
    public void TechnicalDetailsAreCollapsedByDefault()
    {
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() with { Limitations = ["Package wiring outside the archive is not visible."] } };
        var cut = Panel();
        foreach (var id in new[] { "scim-config-details", "scim-source-details", "scim-empty-help" })
        {
            cut.Find($"[data-testid='{id}-toggle']").GetAttribute("aria-expanded").Should().Be("false", id);
            cut.Find($"[data-testid='{id}-body']").HasAttribute("hidden").Should().BeTrue(id);
        }
        cut.Find("[data-testid='scim-config-details-body']").TextContent.Should().Contain("Microsoft.Azure.SyncFabric").And.Contain("ServiceBus__FQDN");
        cut.Find("[data-testid='scim-config-details-toggle']").Click();
        cut.Find("[data-testid='scim-config-details-toggle']").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid='scim-config-details-body']").HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void SourceNotAnalyzedHasItsOwnStateAndIsNotMissingImplementation()
    {
        var cut = Panel();
        cut.Find("[data-testid='scim-source-state'] .scim-badge").TextContent.Should().Be("Not analyzed");
        cut.Find("[data-testid='scim-source-summary']").TextContent.Should().Contain("not evidence that an implementation is missing").And.Contain("not that it ran");
        cut.Find("[data-testid='scim-source-section'] [data-testid='scimsrc-primary']").Should().NotBeNull("the snapshot choice sits with the source evidence");
        cut.Find("[data-testid='scim-source-section']").TextContent.Should().Contain("Use SCIM evidence from this snapshot");
        cut.FindAll("[data-testid='scim-source-section'] input[type=file]").Should().BeEmpty("source is uploaded only in Source Analysis");
        cut.FindAll("[data-testid='scim-source-provenance']").Should().BeEmpty();
    }

    [Fact]
    public void SourceAnalyzedShowsSourceEvidenceButRuntimeStaysNotAssessed()
    {
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() };
        var cut = Panel();
        cut.Find("[data-testid='scim-source-state'] .scim-badge").TextContent.Should().Be("Analyzed");
        cut.Find("[data-testid='scim-source-summary']").TextContent.Should().Contain("SCIM detected in M2LB.Autorisasjon.ScimAdapter: 2 operation(s)");
        cut.Find("[data-testid='scim-source-provenance']").TextContent.Should().Contain("M2LB (1).zip").And.Contain("M2LB.Autorisasjon.ScimAdapter");
        cut.Find("[data-testid='scim-source-section']").TextContent.Should().Contain("Replace SCIM evidence with this snapshot");
        Dimensions(Node(cut, ScimStage.ScimEndpoint)).Should().Be(("Configured", "Source verified", "Not assessed"));
        Dimensions(Node(cut, ScimStage.KjentBrukerPersistence)).Should().Be(("Configured", "Source verified", "Not assessed"));
        Dimensions(Node(cut, ScimStage.ServiceBusPublish)).Should().Be(("Configured", "Source verified", "Not assessed"));
        Dimensions(Node(cut, ScimStage.DownstreamProcessing)).Runtime.Should().Be("Not assessed");
        cut.Find("[data-testid='scim-source-section']").TextContent.Should().NotContainAny("Observed", "Runtime evidence", "End-to-end");
        cut.Find("[data-testid='scim-empty']").TextContent.Should().Contain("No SCIM review has been run yet.");
    }

    [Fact]
    public void BaseUrlUnknownIsLimitedNeverFailedAndOnlySourceAndConfigurationCanBeAssessed()
    {
        var cut = Panel();
        var status = cut.Find("[data-testid='scim-safe-status']");
        status.TextContent.Should().Be("Limited");
        status.ClassList.Should().Contain("scim-badge-attention").And.NotContain("scim-badge-fail");
        cut.Find("[data-testid='scim-safe-reason']").TextContent.Should().Be("Public SCIM base URL is unknown.");
        cut.Find("[data-testid='scim-safe-impact']").TextContent.Should().Contain("Only source and configuration evidence can currently be assessed").And.Contain("cannot yet be verified");
        cut.Find("[data-testid='scim-run-scope']").TextContent.Should().Contain("no SCIM endpoint is called");
        cut.Find("[data-testid='scim-set-base-url']").Click();
        cut.Find("[data-testid='scim-base-url']").Should().NotBeNull("the base URL is set in the SCIM settings form");
        cut.Find("[data-testid='scim-runtime-section']").TextContent.Should().NotContainAny("SCIM unavailable", "Failed", "Misconfigured");
    }

    [Fact]
    public void BaseUrlConfiguredMakesSafeChecksAvailableGetOnly()
    {
        var cut = Panel(baseUrl: "https://scim.example.test");
        cut.Find("[data-testid='scim-safe-status']").TextContent.Should().Be("Available");
        cut.FindAll("[data-testid='scim-safe-reason']").Should().BeEmpty();
        cut.Find("[data-testid='scim-safe-impact']").TextContent.Should().Contain("GET only");
        cut.Find("[data-testid='scim-run-scope']").TextContent.Should().Contain("never creates, changes, deletes or lists a user");
        cut.FindAll("[data-testid='scim-set-base-url']").Should().BeEmpty();
    }

    [Fact]
    public void ProductionIsNeverOfferedAsASafeCheckTarget()
    {
        var cut = Panel(FrontendEnvironmentType.Production, "https://scim.example.test");

        cut.Find("[data-testid='scim-safe-status']").TextContent.Should().Be("Not available");
        cut.Find("[data-testid='scim-safe-detail']").TextContent.Should().Contain("Production is never contacted");
    }

    [Fact]
    public void AStoredRunShowsTheRuntimeResultWithoutTurningConfigurationIntoRuntime()
    {
        _api.ScimCheck = _ => Check();
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() };
        var cut = Panel();
        cut.Find("[data-testid='scim-run']").Click();

        cut.Find("[data-testid='scim-safe-last']").TextContent.Should().Contain("Runtime: Not configured — No SCIM base URL is configured.");
        Dimensions(Node(cut, ScimStage.KjentBrukerPersistence)).Should().Be(("Configured", "Source verified", "Not tested"));
        Dimensions(Node(cut, ScimStage.ScimEndpoint)).Runtime.Should().Be("Not assessed", "no base URL is a missing capability, not a SCIM misconfiguration");
        Dimensions(Node(cut, ScimStage.ServiceBusPublish)).Configuration.Should().Be("Configured");
        Dimensions(Node(cut, ScimStage.DownstreamProcessing)).Source.Should().Be("Not found");
        cut.FindAll("[data-testid='scim-empty']").Should().BeEmpty();
        cut.Find("[data-testid='scim-export']").Should().NotBeNull();
    }

    [Fact]
    public void SyntheticMutationTestIsSeparateOptionalAndNeverRunAutomatically()
    {
        var cut = Panel();
        var synthetic = cut.Find("[data-testid='scim-synthetic']");
        synthetic.QuerySelector("[data-testid='scim-mutation-status']")!.TextContent.Should().Be("Not configured");
        synthetic.QuerySelector("[data-testid='scim-mutation-status']")!.ClassList.Should().Contain("scim-badge-muted");
        cut.Find("[data-testid='scim-mutation-purpose']").TextContent.Should().Contain("Controlled runtime mutation verification").And.Contain("not required for the SCIM review").And.Contain("never run automatically");
        synthetic.QuerySelector("[data-testid='scim-configure']")!.TextContent.Should().Be("Configure synthetic test context");
        cut.Find("[data-testid='scim-safe']").QuerySelector("[data-testid='scim-configure']").Should().BeNull("the synthetic test is apart from the safe checks");
        _api.Calls.Should().NotContain(c => c.StartsWith("scim-checks:"), "rendering never runs a check");
    }

    [Fact]
    public void SyntheticMutationTestConfiguredIsStillNotExecuted()
    {
        var configured = new ScimSyntheticTestContext { Environment = "QA", TestUserPrefix = ScimSyntheticTestContext.RequiredPrefix, CleanupPlan = "Delete the synthetic user", ApprovedByTestLead = true, Enabled = true };
        PanelWith(configured).Find("[data-testid='scim-mutation-status']").TextContent.Should().Be("Not available in this version");
        PanelWith(configured with { Enabled = false }).Find("[data-testid='scim-mutation-status']").TextContent.Should().Be("Disabled");
    }

    [Fact]
    public void DeactivationWordingNeverImpliesRevokedAccess()
    {
        var cut = Panel();
        var note = cut.Find("[data-testid='scim-limitation']");
        note.GetAttribute("role").Should().Be("note");
        note.TextContent.Should().Contain("Source and configuration evidence do not prove runtime success.").And.Contain("A published deactivation event does not prove that access was revoked.");
        note.ClassList.Should().NotContain("scim-error");
        cut.Markup.Should().NotContainAny("Access revoked", "access revoked", "Revoked access", "Deactivation verified");
    }

    [Fact]
    public void ActionsAreGroupedByPurposeAndAreNativeControls()
    {
        var cut = Panel();
        cut.Find("[data-testid='scim-source-section'] [data-testid='scim-use-scope']").TagName.Should().Be("BUTTON");
        cut.Find("[data-testid='scim-source-section'] [data-testid='scimsrc-primary']").ParentElement!.TagName.Should().Be("LABEL");
        var run = cut.Find("[data-testid='scim-runtime-section'] [data-testid='scim-safe'] [data-testid='scim-run']");
        run.TagName.Should().Be("BUTTON");
        run.GetAttribute("aria-label").Should().Contain("GET only");
        cut.Find("[data-testid='scim-runtime-section'] [data-testid='scim-synthetic'] [data-testid='scim-configure']").TagName.Should().Be("BUTTON");
        cut.FindAll("[data-testid='scim-flow'] button, [data-testid='scim-flow'] a, [data-testid='scim-flow'] [tabindex]").Should().BeEmpty("the flow is static: no nested interactive controls");
        cut.Find("[data-testid='scim-empty']").TextContent.Should().Contain("No SCIM review has been run yet.");
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
    public void UsingASnapshotRecordsItsScimEvidenceAndReportsDetection()
    {
        _api.ScimOverview = new ScimEvidenceOverview { Source = Source() };
        var cut = Panel();

        cut.Find("[data-testid='scimsrc-primary']").Change(_api.SourceOptions.Snapshots[0].SnapshotId.ToString());
        cut.Find("[data-testid='scim-use-scope']").Click();

        _api.UsedScopes.Should().ContainSingle(s => s.PrimarySnapshotId == _api.SourceOptions.Snapshots[0].SnapshotId && s.RelatedSnapshotIds.Count == 0);
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
