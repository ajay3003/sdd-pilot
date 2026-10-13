using System.Net;
using System.Text.RegularExpressions;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Altinn setup shows what is configured versus discovered, warns concisely when the deployed contract changed since it was confirmed
/// (with a Before/After/Impact review), never shows a reporter identifier, and keeps an imported OpenAPI document labelled as not live-verified.
/// </summary>
public sealed class AltinnDynamicConfigurationUiTests : BunitContext
{
    private readonly FakeApi _api = new();

    public AltinnDynamicConfigurationUiTests()
    {
        Services.AddSingleton<IAltinnTestingApiService>(_api);
        Services.AddSingleton(new ActiveEventAuthenticationState(true, "Configured (test)."));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AltinnTestSetupPanel> Render() => Render<AltinnTestSetupPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));

    private static AltinnContractChangeSet Changes(params (string Area, string Before, string After, AltinnContractChangeImpact Impact)[] items) => new()
    {
        Impact = items.Max(item => item.Impact), Changes = items.Select(item => new AltinnContractChange(item.Area, item.Before, item.After, item.Impact, $"About {item.Area}.")).ToArray(),
    };

    [Fact]
    public void AChangedDeployment_IsAConciseWarning_WithAReviewTable_AndAcceptOnlyWhenUsable()
    {
        _api.Contract = _api.Contract with
        {
            Freshness = AltinnContractFreshness.Changed, FreshnessDetail = "Changed since the confirmed contract · read 2026-10-13 16:05 UTC",
            Changes = Changes(("Task", "Task_1", "Task_2", AltinnContractChangeImpact.NeedsReview), ("App-lib version", "8.12.7.264", "8.13.0.1", AltinnContractChangeImpact.Compatible)),
            ChangesAcceptable = true, ChangesDetail = "2 change(s) since the confirmed contract; the new contract is otherwise usable.",
        };
        var cut = Render();
        cut.Find("[data-testid=atp-contract-changed]").TextContent.Should().Contain("Deployed Altinn contract changed since last confirmation.").And.Contain("Real execution is blocked");
        cut.Find("[data-testid=atp-change-review]").HasAttribute("open").Should().BeFalse("the review is one click away, not a wall of text");
        cut.FindAll("[data-testid=atp-change]").Select(row => row.TextContent).Should().SatisfyRespectively(
            row => row.Should().Contain("Task").And.Contain("Task_1").And.Contain("Task_2").And.Contain("Needs review"),
            row => row.Should().Contain("App-lib version").And.Contain("Informational"));
        cut.Find("[data-testid=atp-freshness]").TextContent.Should().Contain("Deployed contract: Changed");

        cut.Find("[data-testid=atp-accept-changes]").Click();
        _api.Accepted.Should().Be(1);
    }

    [Fact]
    public void ABlockingChange_CannotBeAccepted_AndSaysWhy()
    {
        _api.Contract = _api.Contract with
        {
            Changes = Changes(("Process tasks", "Task_1 · Utfylling · data", "Task_1 · Utfylling · data, Task_2 · Kontroll · data", AltinnContractChangeImpact.Blocking)),
            ChangesAcceptable = false, ChangesDetail = "The changed contract is not usable as it stands: Partial: the transition plan cannot be established.",
        };
        var cut = Render();
        cut.Find("[data-testid=atp-change]").TextContent.Should().Contain("Blocks run");
        cut.Find("[data-testid=atp-accept-changes]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=atp-changes-detail]").TextContent.Should().Contain("not usable as it stands");
    }

    [Theory]
    [InlineData(AltinnContractFreshness.NotRead, "Deployed contract: Not read")]
    [InlineData(AltinnContractFreshness.Current, "Deployed contract: Current")]
    [InlineData(AltinnContractFreshness.RefreshRecommended, "Deployed contract: Refresh recommended")]
    public void Freshness_IsNeverOverstated(AltinnContractFreshness freshness, string label)
    {
        _api.Contract = _api.Contract with { Freshness = freshness, FreshnessDetail = "detail" };
        Render().Find("[data-testid=atp-freshness]").TextContent.Should().Contain(label);
    }

    [Fact]
    public void ConfiguredAndDiscoveredValues_ShowTheirSource_AndTheReporterOnlyByAlias()
    {
        var cut = Render();
        var rows = cut.FindAll("[data-testid=atp-config-item]").ToDictionary(row => row.GetAttribute("data-key")!, row => row.TextContent);
        rows["app"].Should().Contain("buf/skolenaervaer").And.Contain("Target configuration");
        rows["process-task"].Should().Contain("Task_1 · Utfylling · data").And.Contain("TT02 deployed OpenAPI contract");
        rows["secret"].Should().Contain("Available").And.Contain("Secret · local environment");
        rows["scopes"].Should().Contain("3 configured").And.Contain("Not checked against the client registration");
        rows["child"].Should().Contain("Declared").And.Contain("Run input");

        var reporter = cut.Find("[data-testid=atp-reporter]");
        reporter.TextContent.Should().Contain("Skolenærvær reporter A").And.Contain("reporter-a");
        reporter.QuerySelector("[data-testid=atp-reporter-identifier]")!.TextContent.Should().Be("Configured");
        reporter.QuerySelector("[data-testid=atp-reporter-profile]")!.TextContent.Should().Be("Not confirmed");
        Regex.IsMatch(cut.Markup, @"\b\d{11}\b").Should().BeFalse("no reporter or child identifier is ever rendered");
    }

    [Fact]
    public async Task AnImportedOpenApi_IsUploaded_AndShownAsNotLiveVerified()
    {
        var cut = Render();
        cut.Find("[data-testid=atp-source-public]").Change(true);
        await cut.InvokeAsync(() => cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("{\"openapi\":\"3.0.4\"}", "customOpenapi.json")));
        _api.Imported.Should().Equal("{\"openapi\":\"3.0.4\"}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=atp-imported]").TextContent.Should().Contain("Imported OpenAPI").And.Contain("Not live-verified").And.Contain("Task_1 → Task_2"));
    }

    private sealed class FakeApi : IAltinnTestingApiService
    {
        public int Accepted { get; private set; }
        public List<string> Imported { get; } = [];
        public AltinnAppContractView Contract { get; set; } = new()
        {
            EnvironmentId = "dev-env", ConfiguredAppId = "buf/skolenaervaer", AppApproved = true, AppDetail = "Approved app buf/skolenaervaer.", Complete = true,
            Freshness = AltinnContractFreshness.Current, FreshnessDetail = "Read 2026-10-13 16:00 UTC",
        };

        public AltinnConfigurationOverview Overview { get; set; } = new()
        {
            EnvironmentId = "dev-env",
            Items =
            [
                new("app", "Altinn app", "buf/skolenaervaer", "Approved", "Target configuration", "Approved."),
                new("process-task", "Process task", "Task_1 · Utfylling · data", "Confirmed for current operation", "TT02 deployed OpenAPI contract", "Discovered."),
                new("secret", "Client secret", null, "Available", "Secret · local environment", "Available to the backend (value never shown)."),
                new("scopes", "Scopes", "openid, altinn:instances.read, altinn:instances.write", "3 configured", "Integration Quality Review", "Not checked against the client registration: BirkNext cannot see it."),
                new("child", "Synthetic child", null, "Declared", "Run input + approved identity set (IQR)", "Declared ≠ verified."),
            ],
            Reporters = [new("reporter-a", "Skolenærvær reporter A", true, false, "PRIV", null)],
        };

        public Task<ActiveEventApiResult<AltinnAppContractView>> ContractAsync(string environmentId, CancellationToken ct = default) => Ok(Contract);
        public Task<ActiveEventApiResult<AltinnAppContractView>> ReadPublicMetadataAsync(string environmentId, CancellationToken ct = default) => Ok(Contract);
        public Task<ActiveEventApiResult<AltinnAppContractView>> UseSourceAnalysisAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default) => Ok(Contract);
        public Task<ActiveEventApiResult<AltinnSignInStatus>> SignInStatusAsync(string environmentId, CancellationToken ct = default) => Ok(new AltinnSignInStatus());
        public Task<ActiveEventApiResult<AltinnSignInStart>> StartSignInAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<AltinnSignInStart>(null, "n/a", HttpStatusCode.BadRequest));
        public Task<ActiveEventApiResult<bool>> SignOutAsync(string environmentId, CancellationToken ct = default) => Ok(true);
        public Task<ActiveEventApiResult<AltinnConfigurationOverview>> ConfigurationAsync(string environmentId, CancellationToken ct = default) => Ok(Overview);
        public Task<ActiveEventApiResult<AltinnAppContractView>> AcceptContractChangesAsync(string environmentId, CancellationToken ct = default)
        {
            Accepted++;
            return Ok(Contract with { Changes = null });
        }
        public Task<ActiveEventApiResult<AltinnAppContractView>> ImportOpenApiAsync(string environmentId, string json, CancellationToken ct = default)
        {
            Imported.Add(json);
            return Ok(Contract with
            {
                ImportedOpenApi = new AltinnContractSnapshot { Source = AltinnContractSourceKind.ImportedOpenApi, AppId = "buf/skolenaervaer", Fingerprint = new string('b', 64),
                    DeployedProcess = new AltinnDeployedProcessContract { AppLibVersion = "8.12.7.264" } },
                ImportedComparedToLive = new AltinnContractChangeSet { Impact = AltinnContractChangeImpact.NeedsReview,
                    Changes = [new AltinnContractChange("Task", "Task_1", "Task_2", AltinnContractChangeImpact.NeedsReview, "Renamed.")] },
            });
        }
        private static Task<ActiveEventApiResult<T>> Ok<T>(T value) => Task.FromResult(new ActiveEventApiResult<T>(value, null, HttpStatusCode.OK));
    }
}
