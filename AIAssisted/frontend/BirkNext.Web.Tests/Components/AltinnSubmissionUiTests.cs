using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Altinn TT02 submission UI: typed scenario inputs in the shared journey panel (no second runner), the Altinn setup on the existing
/// Skolenærvær page (contract source, missing parts, TestID sign-in) and truthful run evidence. No token, secret or full identifier is shown.
/// </summary>
public sealed class AltinnSubmissionUiTests : BunitContext
{
    private const string Identifier = "01821512345";
    private readonly FakeJourneys _journeys = new();
    private readonly FakeAltinn _altinn = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development };

    public AltinnSubmissionUiTests()
    {
        Services.AddSingleton<IIntegrationJourneysApiService>(_journeys);
        Services.AddSingleton<IAltinnTestingApiService>(_altinn);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(new ActiveEventAuthenticationState(true, "Configured (test)."));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void ScenarioInputs_AreRenderedFromTheBackendDescriptor_WithAccessibleLabels_AndRunWaitsForRequiredValues()
    {
        var cut = Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.InitialJourneyId, "rapportmottak"));

        cut.Find("[data-testid=ijp-journey]").GetAttribute("data-journey").Should().Be("rapportmottak", "the page can open a journey directly");
        var inputs = cut.FindAll("[data-testid=ijp-input]");
        inputs.Select(item => item.GetAttribute("data-key")).Should().Equal("identitySource", "childIdentifier", "periodFrom", "periodTo", "plannedHours", "actualHours");
        foreach (var input in inputs)
        {
            var control = input.QuerySelector("input, select")!;
            input.QuerySelector("label")!.GetAttribute("for").Should().Be(control.Id, "every input has a programmatic label");
        }
        cut.Find("[data-key=childIdentifier] input").GetAttribute("autocomplete").Should().Be("off");
        cut.Find("[data-key=periodFrom] input").GetAttribute("type").Should().Be("date");
        cut.Find("[data-key=plannedHours] input").GetAttribute("type").Should().Be("number");
        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeTrue("required inputs are empty");

        Fill(cut);
        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=atp-panel]").Should().BeEmpty("the journey panel is the one runner; the Altinn setup is a separate, non-executing component");
    }

    [Fact]
    public void Running_SendsTypedInputs_ShowsEachStepsOwnEvidence_AndForgetsTheIdentifier()
    {
        var cut = Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.InitialJourneyId, "rapportmottak"));
        Fill(cut);
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-ack]").Change(true);
        cut.Find("[data-testid=ijp-confirm-run]").Click();

        var request = _journeys.Started.Single();
        request.ScenarioId.Should().Be("valid-report");
        request.Inputs.Should().Contain(new KeyValuePair<string, string>("childIdentifier", Identifier)).And.Contain(new KeyValuePair<string, string>("periodTo", "2026-10-31"));
        var last = cut.Find("[data-testid=ijp-last-run]");
        last.GetAttribute("data-state").Should().Be("Partial");
        last.TextContent.Should().Contain("Verified").And.Contain("Not assessed").And.Contain("receipt: instance inst-6a1b2c3d").And.NotContain("Passed");
        cut.Find("[data-testid=ijp-run-inputs]").TextContent.Should().Contain("#1a2b3c4d").And.NotContain(Identifier);
        cut.Find("[data-key=childIdentifier] input").GetAttribute("value").Should().BeNullOrEmpty("the identifier is not kept in the browser after the run");
        cut.Markup.Should().NotContain("token").And.NotContain(Identifier);
    }

    [Fact]
    public void Setup_ShowsTheContractParts_TheMissingInformation_AndTheChainSemantics()
    {
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));

        cut.Find("[data-testid=atp-semantics]").TextContent.Should().Contain("Altinn submitted").And.Contain("MU received").And.Contain("MM processed")
            .And.Contain("Skolenærværsrapport stored").And.Contain("Bufdata received");
        cut.FindAll("[data-testid=atp-part]").Select(row => (row.GetAttribute("data-part"), row.GetAttribute("data-present")))
            .Should().Equal(("ApplicationMetadata", "true"), ("FormDataType", "true"), ("DataModelSchema", "true"), ("ProcessDefinition", "false"));
        cut.Find("[data-testid=atp-contract-state]").TextContent.Should().Contain("Contract incomplete");
        cut.Find("[data-testid=atp-missing]").TextContent.Should().Contain("process.bpmn");
        cut.FindAll("[data-testid=atp-bindings] li").Should().HaveCount(5);
    }

    [Fact]
    public void Setup_OffersReadOnlyInstanceEvidence_ThatOpensTheJourneyWithTheReadScenarioSelected()
    {
        var opened = new List<string?>();
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env").Add(c => c.OnOpenJourney, (string? scenario) => opened.Add(scenario)));

        cut.Find("[data-testid=atp-instance-evidence]").TextContent.Should().Contain("Read-only: no data is submitted or changed.")
            .And.Contain("instance endpoint is not used").And.Contain("not an MU blob").And.Contain("not a verified submission");
        cut.Find("[data-testid=atp-open-instance-evidence]").Click();
        cut.Find("[data-testid=atp-open-journey]").Click();
        opened.Should().Equal("instance-evidence", null);
    }

    [Fact]
    public void Setup_UsesTheAppSourceFromASourceAnalysisSnapshot_WithoutAnyUploadOfItsOwn()
    {
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        cut.FindAll("input[type=file]").Should().BeEmpty("source is uploaded through Source Analysis, never here");
        cut.Find("[data-testid=atp-snapshot]").TextContent.Should().Contain("skolenaervaer-app.zip");
        cut.Find("[data-testid=atp-use-source]").Click();
        _altinn.SourceUses.Should().Equal(FakeAltinn.SnapshotId);
    }

    [Fact]
    public void Setup_ChoosingPublicMetadata_ReadsItWithoutAnyAddressFromTheBrowser()
    {
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        cut.Find("[data-testid=atp-source-public]").Change(true);
        cut.Find("[data-testid=atp-read-public]").Click();
        _altinn.PublicReads.Should().Be(1);
        cut.FindAll("input[type=url], input[name*=url]").Should().BeEmpty("no app URL or endpoint can be entered");
    }

    [Fact]
    public void UnattendedAuthentication_IsTheDefault_WithEachPrerequisiteOnItsOwn_AndNoSignInRequired()
    {
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));

        cut.Find("[data-testid=atp-unattended-intro]").TextContent.Should().Contain("No sign-in step").And.Contain("No browser opens").And.Contain("child in the report is separate");
        var rows = cut.FindAll("[data-testid=atp-readiness-item]").ToDictionary(row => row.GetAttribute("data-key")!, row => row.TextContent);
        rows.Keys.Should().Equal("local-execution", "altinn-environment", "app-policy", "unattended-provider", "idporten-client", "client-secret", "synthetic-reporter", "reporter-auth", "real-execution");
        rows["app-policy"].Should().Contain("Not confirmed").And.Contain("App authorization policy not confirmed");
        rows["client-secret"].Should().Contain("Missing").And.Contain("not available to the backend");
        rows["idporten-client"].Should().Contain("Missing");
        rows["unattended-provider"].Should().Contain("Available");
        rows["synthetic-reporter"].Should().Contain("Configured");
        cut.Markup.Should().NotContain("Authentication not configured", "each prerequisite is named on its own")
            .And.NotContainEquivalentOf("client_secret").And.NotContainEquivalentOf("login_hint").And.NotContainEquivalentOf("bearer");

        var fallback = cut.Find("[data-testid=atp-interactive-fallback]");
        fallback.TagName.Should().Be("DETAILS");
        fallback.HasAttribute("open").Should().BeFalse("the interactive sign-in is a collapsed, secondary fallback");
        cut.Find("[data-testid=atp-sign-in]").ClassList.Should().Contain("btn-secondary").And.NotContain("btn-primary");
        cut.Find("[data-testid=atp-sign-in]").Closest("[data-testid=atp-interactive-fallback]").Should().NotBeNull("the TestID button only exists inside the fallback");
    }

    [Fact]
    public void LocalTrustedExecution_WithoutEntra_ShowsNoEntraPrompt_AndKeepsTheFallbackSecondary()
    {
        Services.AddSingleton(ActiveEventAuthenticationState.NotConfigured);
        Services.AddSingleton<IExecutionAccessApiService>(new FixedAccess(new ExecutionAccessView(ExecutionAccessMode.LocalTrusted, true, "Local trusted execution.")));
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));

        cut.FindAll("[data-testid=atp-sign-in-unavailable]").Should().BeEmpty("local trusted execution is execution access; no Entra sign-in is needed");
        cut.Markup.Should().NotContain("Entra").And.NotContain("Companion").And.NotContain("Dedicated Edge");
        cut.Find("[data-testid=atp-unattended-intro]").TextContent.Should().Contain("No sign-in step");
        cut.Find("[data-testid=atp-interactive-fallback]").HasAttribute("open").Should().BeFalse();
    }

    private sealed class FixedAccess(ExecutionAccessView view) : IExecutionAccessApiService
    {
        public Task<ExecutionAccessView> GetAsync(CancellationToken ct = default) => Task.FromResult(view);
    }

    [Fact]
    public void ValidateRun_ShowsEachCheck_AndARedactedPreview_WithoutStartingARun()
    {
        var cut = Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.InitialJourneyId, "rapportmottak"));
        cut.Find("[data-testid=ijp-validate]").HasAttribute("disabled").Should().BeTrue("inputs are empty");
        Fill(cut);
        cut.Find("[data-testid=ijp-validate]").TextContent.Should().Be("Validate run (no external calls)");
        cut.Find("[data-testid=ijp-validate]").Click();

        _journeys.Validated.Should().ContainSingle().Which.Confirmed.Should().BeFalse("validation is not a confirmed run");
        _journeys.Started.Should().BeEmpty();
        var section = cut.Find("[data-testid=ijp-validation]");
        section.GetAttribute("data-valid").Should().Be("false");
        section.TextContent.Should().Contain("Not valid").And.Contain("Validated without any external call");
        cut.FindAll("[data-testid=ijp-validation-check]").Select(li => (li.GetAttribute("data-key"), li.GetAttribute("data-passed"), li.GetAttribute("data-blocking")))
            .Should().Equal(("endpoints", "true", "true"), ("process-plan", "false", "true"), ("unfilled-required", "false", "false"));
        cut.Find("[data-key=unfilled-required]").TextContent.Should().StartWith("Risk:");
        cut.Find("[data-testid=ijp-validation-preview]").TextContent.Should().Contain("#1a2b3c4d").And.NotContain(Identifier);
    }

    [Fact]
    public void TheConfirmation_StatesThatTheRunIsARealExternalCall_WithOneAttempt()
    {
        var cut = Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.InitialJourneyId, "rapportmottak"));
        Fill(cut);
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-confirm-external]").TextContent.Should().Contain("real external call").And.Contain("one attempt, nothing is retried");
    }

    [Fact]
    public void Readiness_ShowsLocalExecution_ReporterProfile_AndTheRealExecutionVerdict()
    {
        _journeys.Prerequisites.Add(new("reporter-profile", "Reporter Altinn profile", JourneyPrerequisiteCategory.SyntheticData, JourneyReadinessState.NotConfigured,
            "Not confirmed for 1 reporter alias(es).", Mandatory: false));
        var notReady = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        var keys = notReady.FindAll("[data-testid=atp-readiness-item]").Select(li => li.GetAttribute("data-key")).ToList();
        keys.First().Should().Be("local-execution");
        keys.Last().Should().Be("real-execution");
        keys.Should().Contain("reporter-profile");
        notReady.Find("[data-key=reporter-profile]").TextContent.Should().Contain("Not confirmed");
        notReady.Find("[data-key=real-execution]").GetAttribute("data-state").Should().Be("NotReady");

        _journeys.Runnable.Add("valid-report");
        var ready = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        ready.Find("[data-key=real-execution]").GetAttribute("data-state").Should().Be("Ready");
        ready.Find("[data-key=real-execution]").TextContent.Should().Contain("validate it first").And.Contain("One attempt");
    }

    [Fact]
    public void FullyReadyUnattendedReadiness_IsShownAsReady()
    {
        _journeys.Prerequisites.Clear();
        _journeys.Prerequisites.AddRange(
        [
            new("app-policy", "App authorization policy", JourneyPrerequisiteCategory.Authorization, JourneyReadinessState.Ready, "Confirmed by the service owner."),
            new("client-secret", "Client secret", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.Ready, "Available to the backend (value never shown).", Mandatory: false),
            new("reporter-auth", "Reporter authentication", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.Ready, "Unattended."),
        ]);
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        cut.FindAll("[data-testid=atp-readiness-item]").Where(row => row.GetAttribute("data-key") != "real-execution").Should().OnlyContain(row => row.GetAttribute("data-state") == "Ready");
        cut.Find("[data-key=app-policy]").TextContent.Should().Contain("Confirmed");
    }

    [Fact]
    public void SignIn_IsOfferedOnlyWhenConfigured_AndOnlyEverOpensIdPortenTest()
    {
        _altinn.Status = new AltinnSignInStatus { Configured = false, ConfigurationProblems = ["No ID-porten TEST client id is configured."], Detail = "ID-porten TEST sign-in is not configured." };
        var notConfigured = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        notConfigured.Find("[data-testid=atp-sign-in]").HasAttribute("disabled").Should().BeTrue();
        notConfigured.Find("[data-testid=atp-sign-in-problems]").TextContent.Should().Contain("client id");

        _altinn.Status = new AltinnSignInStatus { Configured = true, Detail = "Not signed in." };
        _altinn.StartUrl = "https://login.test.idporten.no/authorize?client_id=x&state=y";
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        cut.Find("[data-testid=atp-sign-in]").Click();
        navigation.Uri.Should().StartWith("https://login.test.idporten.no/authorize");

        _altinn.StartUrl = "https://evil.example/authorize";
        var refused = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        refused.Find("[data-testid=atp-sign-in]").Click();
        refused.Find("[data-testid=atp-error]").TextContent.Should().Contain("not an ID-porten TEST address");
        navigation.Uri.Should().NotContain("evil.example");
    }

    [Fact]
    public void SignedIn_ShowsStatusAndExpiry_ButNeverAToken()
    {
        _altinn.Status = new AltinnSignInStatus { Configured = true, SignedIn = true, ExpiresAt = DateTimeOffset.Parse("2026-10-12T10:30:00Z"), Detail = "Signed in to ID-porten TEST." };
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        cut.Find("[data-testid=atp-sign-in-state]").TextContent.Should().Contain("Signed in").And.Contain("Expires 10:30 UTC");
        cut.FindAll("[data-testid=atp-sign-out]").Should().ContainSingle();
        cut.Markup.Should().NotContainEquivalentOf("bearer").And.NotContainEquivalentOf("access_token");
    }

    [Fact]
    public void ReturningFromIdPortenTest_ReportsTheOutcome()
    {
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/extensions/skolenaervaer?altinn-sign-in=failed");
        var cut = Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));
        cut.Find("[data-testid=atp-sign-in-outcome]").TextContent.Should().Contain("did not complete");
    }

    [Fact]
    public void ThePage_HostsTheSetupUnderRapportmottak_AndIsNotReadyWithoutATarget()
    {
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        var context = new FrontendAnalysisContext();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => context);
        Services.AddSingleton(factory.Object);
        var catalog = new Mock<IIntegrationCatalogApiService>();
        catalog.Setup(a => a.GetCatalogAsync(It.IsAny<FrontendAnalysisProfile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new IntegrationCatalog());
        catalog.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(catalog.Object);

        var without = Render<SkoleNaervaerTesting>();
        without.Find("#snt-rapport [data-testid=snt-altinn-not-ready]").TextContent.Should().StartWith("Not ready");
        without.FindAll("[data-testid=atp-panel]").Should().BeEmpty();

        context = new FrontendAnalysisContext { ActiveProfile = Profile };
        var with = Render<SkoleNaervaerTesting>();
        with.WaitForAssertion(() => with.FindAll("#snt-rapport [data-testid=atp-panel]").Should().ContainSingle());
        with.FindAll("[data-testid=ijp-panel]").Should().ContainSingle("one shared journey panel runs the submission");
    }

    private static void Fill(IRenderedComponent<IntegrationJourneyPanel> cut)
    {
        cut.Find("[data-key=identitySource] select").Change("registered");
        cut.Find("[data-key=childIdentifier] input").Change(Identifier);
        cut.Find("[data-key=periodFrom] input").Change("2026-10-01");
        cut.Find("[data-key=periodTo] input").Change("2026-10-31");
        cut.Find("[data-key=plannedHours] input").Change("100");
        cut.Find("[data-key=actualHours] input").Change("92.5");
    }

    private sealed class FakeAltinn : IAltinnTestingApiService
    {
        public int PublicReads { get; private set; }
        public AltinnSignInStatus Status { get; set; } = new() { Configured = true, Detail = "Not signed in." };
        public string StartUrl { get; set; } = "https://login.test.idporten.no/authorize";

        public static readonly Guid SnapshotId = Guid.Parse("11111111-2222-4333-8444-555555555555");
        private static AltinnAppContractView Contract => new()
        {
            EnvironmentId = "dev-env", ConfiguredAppId = "buf/skolenaervaer", AppApproved = true, AppDetail = "Approved app buf/skolenaervaer.", FormDataTypeId = "model",
            Parts =
            [
                new(AltinnContractPartKind.ApplicationMetadata, true, AltinnContractSourceKind.PublicTt02Metadata, "Application metadata."),
                new(AltinnContractPartKind.FormDataType, true, AltinnContractSourceKind.PublicTt02Metadata, "Form data type model."),
                new(AltinnContractPartKind.DataModelSchema, true, AltinnContractSourceKind.PublicTt02Metadata, "12 schema fields."),
                new(AltinnContractPartKind.ProcessDefinition, false, null, "No process definition; upload process.bpmn."),
            ],
            Bindings =
            [
                new("childIdentifier", "Barnets fødselsnummer", "Klient.Identifikator.Fodselsnummer", "string", true, "Found: string."),
                new("periodFrom", "Dato fra", "Periode.StartDato", "string", true, "Found."), new("periodTo", "Dato til", "Periode.SluttDato", "string", true, "Found."),
                new("plannedHours", "Planned", "SkolenaervaerRegistrering.AntallTimerEtterPlanen", "number", true, "Found."),
                new("actualHours", "Actual", "SkolenaervaerRegistrering.AntallTimerFaktiskNaervaer", "number", true, "Found."),
            ],
            Complete = false, Missing = ["No process definition; add the app's source (with App/config/process/process.bpmn) through Source Analysis — the process is not part of the public metadata."],
            SourceCandidates = [new(SnapshotId, "skolenaervaer-app.zip", DateTimeOffset.Parse("2026-10-12T09:00:00Z"), ["buf/skolenaervaer"])],
        };

        public Task<ActiveEventApiResult<AltinnAppContractView>> ContractAsync(string environmentId, CancellationToken ct = default) => Ok(Contract);
        public Task<ActiveEventApiResult<AltinnAppContractView>> ReadPublicMetadataAsync(string environmentId, CancellationToken ct = default) { PublicReads++; return Ok(Contract); }
        public List<Guid?> SourceUses { get; } = [];
        public Task<ActiveEventApiResult<AltinnAppContractView>> UseSourceAnalysisAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default) { SourceUses.Add(snapshotId); return Ok(Contract); }
        public Task<ActiveEventApiResult<AltinnSignInStatus>> SignInStatusAsync(string environmentId, CancellationToken ct = default) => Ok(Status);
        public Task<ActiveEventApiResult<AltinnSignInStart>> StartSignInAsync(string environmentId, CancellationToken ct = default) => Ok(new AltinnSignInStart(StartUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
        public Task<ActiveEventApiResult<bool>> SignOutAsync(string environmentId, CancellationToken ct = default) => Ok(true);
        private static Task<ActiveEventApiResult<T>> Ok<T>(T value) => Task.FromResult(new ActiveEventApiResult<T>(value, null, HttpStatusCode.OK));
    }

    private sealed class FakeJourneys : IIntegrationJourneysApiService
    {
        public List<IntegrationJourneyRunRequest> Started { get; } = [];
        public List<IntegrationJourneyRunRequest> Validated { get; } = [];
        public List<string> Runnable { get; } = [];

        public Task<ActiveEventApiResult<JourneyRunValidation>> ValidateAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
        {
            Validated.Add(request);
            return Task.FromResult(new ActiveEventApiResult<JourneyRunValidation>(new JourneyRunValidation
            {
                PackId = request.PackId, JourneyId = request.JourneyId, ScenarioId = request.ScenarioId, EnvironmentId = request.EnvironmentId, Valid = false,
                Checks =
                [
                    new("endpoints", "Endpoint allowlist", true, "5 endpoints (ID-porten TEST, Altinn TT02 exchange, the approved app) all pass the request guard."),
                    new("process-plan", "Process plan", false, "Not known: the app's process definition (process.bpmn) is not in the contract, so process/next is never called."),
                    new("unfilled-required", "Required schema fields BirkNext does not fill", false, "6 required field(s) are not filled by BirkNext (Meldingshode.Id, …).", false),
                ],
                Preview = [new("XML root", "Skolenaervaer"), new("Klient.Identifikator.Fodselsnummer", "synthetic identifier #1a2b3c4d (masked)")],
            }, null, HttpStatusCode.OK));
        }

        private static IReadOnlyList<JourneyScenarioInput> Inputs =>
        [
            new("identitySource", "Child identity", JourneyScenarioInputKind.Choice, true, "Synthetic only.", [new("registered", "Registered synthetic test person (known child)")]),
            new("childIdentifier", "Barnets fødselsnummer (synthetic)", JourneyScenarioInputKind.SyntheticPersonIdentifier, false, "Never stored."),
            new("periodFrom", "Dato fra", JourneyScenarioInputKind.Date, true), new("periodTo", "Dato til", JourneyScenarioInputKind.Date, true),
            new("plannedHours", "Antall timer etter planen for perioden", JourneyScenarioInputKind.Number, true),
            new("actualHours", "Antall timer faktisk nærvær for perioden", JourneyScenarioInputKind.Number, true),
        ];

        /// <summary>Rapportmottak readiness as the backend reports it (synthetic states; no secret or identifier is representable).</summary>
        public List<JourneyPrerequisite> Prerequisites { get; } =
        [
            new("altinn-environment", "Altinn TT02 environment", JourneyPrerequisiteCategory.TrustedEnvironment, JourneyReadinessState.Ready, "TT02 with Production blocked."),
            new("app-policy", "App authorization policy", JourneyPrerequisiteCategory.Authorization, JourneyReadinessState.NotReady, "App authorization policy not confirmed: no service-owner confirmation."),
            new("unattended-provider", "Unattended ID-porten TEST provider", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.Ready, "Available.", Mandatory: false),
            new("idporten-client", "ID-porten TEST client", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.NotConfigured, "Missing: client id.", Mandatory: false),
            new("client-secret", "Client secret", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.NotConfigured, "Missing: the ID-porten TEST client secret is not available to the backend.", Mandatory: false),
            new("synthetic-reporter", "Synthetic reporter", JourneyPrerequisiteCategory.SyntheticData, JourneyReadinessState.Ready, "1 approved synthetic TestID reporter alias(es) configured.", Mandatory: false),
            new("reporter-auth", "Reporter authentication", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.NotConfigured, "Neither mode is configured."),
        ];

        private IntegrationJourneyPackView Pack => new()
        {
            PackId = "skolenaervaer", DisplayName = "Skolenærvær Testing", EnvironmentId = "dev-env", EnvironmentTrusted = true,
            Journeys =
            [
                new IntegrationJourneyView
                {
                    Journey = new IntegrationJourneyDefinition
                    {
                        PackId = "skolenaervaer", JourneyId = "rapportmottak", DisplayName = "Rapportmottak", ExecutionMode = JourneyExecutionMode.JourneyRunner,
                        Steps = [new("altinn-auth", "Authenticated", JourneyStepKind.ExternalAuthentication, "Tester (TestID)", "Token accepted"), new("mu-receive", "MU receives", JourneyStepKind.MessageTransport, "MU", "Stored")],
                        Scenarios = [new("valid-report", "E2E-01 / MU-01 · Valid report", "Typed report.", JourneyScenarioSupport.Supported, Inputs: Inputs)],
                    },
                    Readiness = JourneyReadinessState.Ready, CanRun = true, ReadinessSummary = "Ready to run after confirmation.", Prerequisites = Prerequisites,
                    RunnableScenarios = Runnable,
                },
            ],
        };

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([Pack], null, HttpStatusCode.OK));
        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(null, "n/a", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(new IntegrationJourneyRun
            {
                RunId = Guid.NewGuid(), PackId = request.PackId, JourneyId = request.JourneyId, ScenarioId = request.ScenarioId, OverallState = JourneyRunState.Partial,
                StateReason = "1 of 2 mandatory steps verified; the rest were accepted/observed only or not verified.",
                InputSummary = ["Child: synthetic identifier #1a2b3c4d — declared registered synthetic test person (known child; not verified by BirkNext)."],
                Steps =
                [
                    new() { StepId = "altinn-receipt", Label = "Altinn receipt read back", State = JourneyStepState.Verified, Evidence = "HTTP 200 · receipt: instance inst-6a1b2c3d · completed 2026-10-12 10:00:00 UTC", Reason = "Altinn receipt." },
                    new() { StepId = "mu-receive", Label = "MU receives", State = JourneyStepState.NotAssessed, Reason = "No executor or observer is registered for this step." },
                ],
            }, null, HttpStatusCode.OK));
        }
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not found.", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>([], null, HttpStatusCode.OK));
    }
}
