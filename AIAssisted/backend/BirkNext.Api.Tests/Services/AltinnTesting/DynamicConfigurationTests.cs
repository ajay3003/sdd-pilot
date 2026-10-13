using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// The Skolenærvær/Altinn setup adapts when its values change, without a code change: reporters are configuration (alias ≠ identifier), the
/// process task, data type and App API paths come from the deployed contract, a changed deployment or policy is detected and never silently
/// relied on, an imported OpenAPI document is offline evidence only, and scopes and the client id are configuration. The observed TT02 values
/// (Task_1, the current data type, app-lib 8.12.7) appear here only as test fixtures next to equivalent alternatives.
/// </summary>
public sealed class DynamicConfigurationTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 16, 0, 0, TimeSpan.Zero);

    // ── Reporter ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AReporter_CanBeReplaced_OrAdded_ByConfigurationAlone_AndTheIdentifierNeverLeavesTheBackend()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var first = AltinnTestKit.ReporterIdentifier;
        var second = SyntheticPersonIdentifier.Generate();
        while (second == first) second = SyntheticPersonIdentifier.Generate();
        kit.Options.UnattendedIdPortenTest.Reporters[0].TestIdPersonIdentifier = second;
        kit.Options.UnattendedIdPortenTest.Reporters.Add(new SyntheticReporterOptions { Alias = "reporter-b", Label = "Skolenærvær reporter B", TestIdPersonIdentifier = first, AltinnProfileConfirmed = true });

        var overview = Overview(harness, kit);
        overview.Reporters.Select(item => (item.Alias, item.Label, item.IdentifierConfigured, item.ProfileConfirmed))
            .Should().Equal(("reporter-a", "Skolenærvær reporter A", true, true), ("reporter-b", "Skolenærvær reporter B", true, true));
        var json = JsonSerializer.Serialize(overview);
        json.Should().NotContain(first).And.NotContain(second).And.NotContain(AltinnTestKit.ClientSecret);

        foreach (var alias in new[] { "reporter-a", "reporter-b" })
        {
            var validation = await Service(harness, kit).ValidateAsync(Run(Report(alias)), default);
            validation.Checks.Single(check => check.Key == "reporter").Passed.Should().BeTrue(alias);
            JsonSerializer.Serialize(validation).Should().NotContain(first).And.NotContain(second);
        }
        // The alias is the stable name; the identifier behind it is what the reporter-is-child check compares.
        kit.Policy.IsReporterIdentity("reporter-a", second).Should().BeTrue();
        kit.Policy.IsReporterIdentity("reporter-a", first).Should().BeFalse("the identifier moved to reporter-b; the alias did not move with it");
    }

    [Fact]
    public async Task ADifferentChild_IsAccepted_AndTheReporterAsChild_IsRefused()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var child = SyntheticPersonIdentifier.Generate();
            if (child == AltinnTestKit.ReporterIdentifier) continue;
            (await Service(harness, kit).ValidateAsync(Run(Report(child: child)), default)).Checks.Single(check => check.Key == "reporter-child").Passed.Should().BeTrue();
        }
        (await Service(harness, kit).ValidateAsync(Run(Report(child: AltinnTestKit.ReporterIdentifier)), default)).Checks.Single(check => check.Key == "reporter-child").Passed.Should().BeFalse();
    }

    // ── Deployed contract: discovered, not hardcoded ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEquivalentSingleDataTaskWithAnotherId_IsAccepted_AndTheRunChecksTheLiveInstanceAgainstThatTask()
    {
        var kit = new AltinnTestKit();
        kit.Http.MetadataJson = AltinnTestKit.ApplicationMetadata(formTask: "Task_7");
        kit.Http.OpenApiJson = AltinnTestKit.DeployedOpenApi(taskRows: "| Task_7 | Registrering | data |  |", formTask: "Task_7");
        kit.Http.LiveInstanceJson = FakeAltinn.InstanceWith(FakeAltinn.ActiveProcess("Task_7"));
        await using var harness = await ReadyAsync(kit);

        var contract = Contract(harness, kit);
        contract.Process.Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation);
        contract.Process.PlannedTasks.Single().Should().Be(new AltinnDeclaredProcessTask("Task_7", "Registrering", "data", []));

        var run = await Service(harness, kit).StartAsync(Run(Report()), "local-session:loopback", default);
        run.Steps.Single(step => step.StepId == "altinn-submit").Facts.Single(fact => fact.Label == "Live task before transition").Value.Should().Be("Task_7 · data");
        kit.Http.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/process/next", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task TheSubmissionUsesTheDeployedPaths_AndAChangedPathIsDetected_ReviewedAndThenUsed()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        Contract(harness, kit).Operations.Should().HaveCount(AltinnAppOperationKeys.Required.Length);
        Contract(harness, kit).OperationsSource.Should().Be(AltinnAppEndpoints.DeployedSource);

        // The app is redeployed with renamed path parameters: the paths change, the operations stay the same.
        kit.Http.OpenApiJson = AltinnTestKit.DeployedOpenApi(partyParam: "partyId");
        var changed = await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        changed.Changes!.Impact.Should().Be(AltinnContractChangeImpact.NeedsReview);
        changed.Changes.Changes.Should().Contain(change => change.Area == "Operation validate" && change.After.Contains("{partyId}", StringComparison.Ordinal));
        changed.Freshness.Should().Be(AltinnContractFreshness.Changed);
        changed.Complete.Should().BeFalse();
        changed.Missing.Should().Contain(item => item.StartsWith("Deployed Altinn contract changed since last confirmation", StringComparison.Ordinal));
        changed.ChangesAcceptable.Should().BeTrue();
        (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak")
            .RunnableScenarios.Should().NotContain("valid-report", "a deployment change needs review before a real run");
        _ = await Service(harness, kit).StartAsync(Run(Report()), "local-session:loopback", default);
        kit.Http.Calls.Should().NotContain(call => call.Uri.Host == "buf.apps.tt02.altinn.no" && call.Uri.AbsolutePath.Contains("/instances", StringComparison.Ordinal), "nothing is sent before the change is reviewed");

        var accepted = await kit.Contracts(harness.MessageFlows, harness.Sources).AcceptChangesAsync(Env, "test reviewer", default);
        accepted.Complete.Should().BeTrue();
        accepted.Confirmed!.Basis.Should().Contain("reviewed");
        accepted.Operations.Should().OnlyContain(operation => operation.Key == AltinnAppOperationKeys.CreateInstance || operation.PathTemplate.Contains("{partyId}", StringComparison.Ordinal));
        kit.Http.Calls.Clear();
        var run = await Service(harness, kit).StartAsync(Run(Report()), "local-session:loopback", default);
        run.Steps.Take(6).Should().OnlyContain(step => step.State == JourneyStepState.Verified, string.Join(" | ", run.Steps.Select(step => $"{step.StepId}: {step.State} {step.Reason}")));
    }

    [Fact]
    public async Task ARenamedOperation_IsNotGuessed_ItBlocks()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        kit.Http.OpenApiJson = AltinnTestKit.DeployedOpenApi(validatePath: "validation");
        var changed = await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        changed.Changes!.Changes.Should().Contain(change => change.Area == "Operation validate" && change.After == "not declared" && change.Impact == AltinnContractChangeImpact.Blocking);
        changed.Missing.Should().Contain(item => item.Contains("does not declare the validate operation", StringComparison.Ordinal));
        changed.ChangesAcceptable.Should().BeFalse();
    }

    [Theory]
    [InlineData("second-task", "Process tasks")]
    [InlineData("action", "Task actions")]
    [InlineData("no-next", "process/next")]
    [InlineData("type", "Task type")]
    public async Task ABlockingDeploymentChange_KeepsTheRealRunBlocked_AndCannotBeAccepted(string variant, string area)
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        kit.Http.OpenApiJson = variant switch
        {
            "second-task" => AltinnTestKit.DeployedOpenApi(taskRows: AltinnTestKit.RealTaskRow + "\n| Task_2 | Kontroll | data |  |"),
            "action" => AltinnTestKit.DeployedOpenApi(taskRows: "| Task_1 | Utfylling | data | submit |"),
            "no-next" => AltinnTestKit.DeployedOpenApi(processNext: false),
            _ => AltinnTestKit.DeployedOpenApi(taskRows: "| Task_1 | Utfylling | confirmation |  |"),
        };
        var changed = await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        changed.Changes!.Impact.Should().Be(AltinnContractChangeImpact.Blocking);
        changed.Changes.Changes.Should().Contain(change => change.Area == area && change.Impact == AltinnContractChangeImpact.Blocking);
        changed.Complete.Should().BeFalse();
        changed.ChangesAcceptable.Should().BeFalse("the changed contract does not satisfy the bounded operation");
        await FluentActions.Invoking(() => kit.Contracts(harness.MessageFlows, harness.Sources).AcceptChangesAsync(Env, null, default)).Should().ThrowAsync<AltinnContractException>();
    }

    [Fact]
    public async Task ANewAppLibVersion_WithTheSameFacts_IsCompatible_AndNeedsNoReview()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        kit.Http.OpenApiJson = AltinnTestKit.DeployedOpenApi(appLib: "8.13.0.1");
        var refreshed = await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        refreshed.Changes.Should().BeNull();
        refreshed.Complete.Should().BeTrue();
        refreshed.Confirmed!.Basis.Should().StartWith("Compatible change accepted automatically").And.Contain("App-lib version");
        refreshed.Freshness.Should().Be(AltinnContractFreshness.Current);
    }

    [Fact]
    public void DataTypeAndTaskOwnershipChanges_NeedReview()
    {
        var before = new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeId = "form_v2", FormDataTaskId = "Task_1", AllowedContentTypes = ["application/xml"], Fingerprint = "a" };
        var changes = AltinnContractComparer.Compare(before, before with { FormDataTypeId = "form_v3", FormDataTaskId = "Task_2", Fingerprint = "b" }, Now);
        changes.Impact.Should().Be(AltinnContractChangeImpact.NeedsReview);
        changes.Changes.Select(change => (change.Area, change.Before, change.After)).Should().Contain([("Form data type", "form_v2", "form_v3"), ("Form data task", "Task_1", "Task_2")]);
        AltinnContractComparer.Compare(before, before with { AppId = "buf/other" }, Now).Impact.Should().Be(AltinnContractChangeImpact.Blocking);
        AltinnContractComparer.Compare(before, before, Now).Impact.Should().Be(AltinnContractChangeImpact.NoChange);
    }

    [Fact]
    public void AnOperationPathOutsideTheApprovedApp_IsNeverUsed()
    {
        var declared = new[] { ("POST", "/buf/skolenaervaer/instances"), ("GET", "/evil/app/instances/{a}/{b}/validate"), ("PUT", "/buf/skolenaervaer/instances/{a}/{b}/process/next") };
        var operations = AltinnDeployedOpenApiReader.Operations(declared, AltinnTestKit.AppId);
        operations.Select(item => item.Key).Should().Equal(AltinnAppOperationKeys.CreateInstance, AltinnAppOperationKeys.ProcessNext);

        var endpoints = AltinnAppEndpoints.Create(new Uri(AltinnTestKit.AppBase), AltinnTestKit.AppId,
            [new AltinnAppOperation(AltinnAppOperationKeys.Validate, "GET", "/other/app/instances/{p}/{i}/validate")], AltinnAppEndpoints.DeployedSource);
        FluentActions.Invoking(() => endpoints.Validate(1, Guid.NewGuid())).Should().Throw<AltinnContractException>().WithMessage("*outside the approved app*");
        var good = AltinnAppEndpoints.Create(new Uri(AltinnTestKit.AppBase), AltinnTestKit.AppId, AltinnAppEndpoints.Defaults(AltinnTestKit.AppId), AltinnAppEndpoints.DefaultSource);
        good.ProcessNext(5, AltinnTestKit.InstanceGuid).ToString().Should().Be($"{AltinnTestKit.AppBase}instances/5/{AltinnTestKit.InstanceGuid:D}/process/next");
        good.ProcessNext(5, AltinnTestKit.InstanceGuid).Host.Should().Be("buf.apps.tt02.altinn.no");
    }

    // ── Imported OpenAPI: offline evidence, never runtime truth ────────────────────────────────────────────────

    [Fact]
    public async Task AnImportedOpenApi_IsComparedWithTheLiveContract_ButNeverExecuted()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var view = await kit.Contracts(harness.MessageFlows, harness.Sources)
            .ImportOpenApiAsync(Env, AltinnTestKit.DeployedOpenApi(taskRows: AltinnTestKit.RealTaskRow + "\n| Task_2 | Kontroll | data |  |"), default);
        view.ImportedOpenApi!.Source.Should().Be(AltinnContractSourceKind.ImportedOpenApi);
        view.ImportedComparedToLive!.Impact.Should().Be(AltinnContractChangeImpact.Blocking);
        view.Process.Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation, "the live contract is still what counts");
        view.Complete.Should().BeTrue();
        view.Changes.Should().BeNull("an import is not a deployment change");
    }

    [Fact]
    public async Task AnImportedOpenApiAlone_DoesNotConfirmAnything()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var view = await kit.Contracts(flows).ImportOpenApiAsync(Env, AltinnTestKit.DeployedOpenApi(), default);
        view.ImportedOpenApi.Should().NotBeNull();
        view.Process.Status.Should().Be(AltinnProcessContractStatus.NotConfirmed);
        view.Freshness.Should().Be(AltinnContractFreshness.NotRead);
        view.Complete.Should().BeFalse();
        kit.Http.Calls.Should().BeEmpty("an import makes no network call");
    }

    // ── Policy ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AChangedPolicyHash_InvalidatesTheConfirmation_AndTheSameHashKeepsIt()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit, policySha: AltinnTestKit.PolicySha256());
        var observed = harness.MessageFlows.Contract!.ObservedPolicy!;
        observed.Sha256.Should().Be(AltinnTestKit.PolicySha256());
        kit.Policy.AppPolicy(AltinnTestKit.AppId, observed, out var same).Should().NotBeNull();
        same.Should().Contain("Matches the deployed policy");

        kit.Http.PolicyXml = AltinnTestKit.PolicyXml.Replace("test", "changed", StringComparison.Ordinal);
        await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        kit.Policy.AppPolicy(AltinnTestKit.AppId, harness.MessageFlows.Contract!.ObservedPolicy, out var changed).Should().BeNull();
        changed.Should().Contain("changed since it was confirmed").And.Contain("Needs confirmation");
        var view = (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");
        view.Prerequisites.Single(item => item.Key == "app-policy").State.Should().Be(JourneyReadinessState.NotReady);
        view.RunnableScenarios.Should().NotContain("valid-report");
        Overview(harness, kit).Items.Single(item => item.Key == "policy").State.Should().Be("Changed · needs confirmation");
    }

    [Fact]
    public async Task AnUnreadablePolicy_IsUnknown_NotUnchanged()
    {
        var kit = new AltinnTestKit();
        kit.Http.PolicyXml = null;
        await using var harness = await ReadyAsync(kit, policySha: AltinnTestKit.PolicySha256());
        var observed = harness.MessageFlows.Contract!.ObservedPolicy!;
        observed.Sha256.Should().BeNull();
        observed.Problem.Should().Be("HTTP 404");
        kit.Policy.AppPolicy(AltinnTestKit.AppId, observed, out var detail).Should().NotBeNull();
        detail.Should().Contain("could not be re-read").And.Contain("unknown");
    }

    // ── Client and scopes: configuration ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScopesAndTheClientId_AreConfiguration_NeverClaimedVerified()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration with { ConfiguredScopes = ["openid", "altinn:instances.write"] };
        var scopes = Overview(harness, kit).Items.Single(item => item.Key == "scopes");
        (scopes.Value, scopes.State).Should().Be(("openid, altinn:instances.write", "2 configured"));
        scopes.Detail.Should().Contain("Not checked against the client registration");

        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration with { ConfiguredScopes = ["altinn:instances.read"] };
        Overview(harness, kit).Items.Single(item => item.Key == "scopes").State.Should().Be("Missing openid");
        kit.Policy.UnattendedProblems(harness.MessageFlows.Configuration).Should().Contain(item => item.Contains("openid"));

        // The client id may come from local backend configuration instead; both set and different is a conflict, never a silent choice.
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration with { ClientReference = null };
        kit.Options.UnattendedIdPortenTest.ClientId = "local-client";
        kit.Policy.ClientId(harness.MessageFlows.Configuration, out var source, out _).Should().Be("local-client");
        source.Should().Be("Local backend configuration");
        kit.Policy.ClientId(AltinnTestKit.ReadyConfiguration, out _, out var conflict).Should().BeNull();
        conflict.Should().Contain("differs");
        Overview(harness, kit, AltinnTestKit.ReadyConfiguration).Items.Single(item => item.Key == "client").State.Should().Be("Conflict");
    }

    [Fact]
    public async Task TheOverview_SaysWhereEachValueComesFrom()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var items = Overview(harness, kit).Items.ToDictionary(item => item.Key);
        (items["app"].Value, items["app"].Source).Should().Be((AltinnTestKit.AppId, AltinnConfigurationOverviewService.TargetConfiguration));
        (items["host"].Value, items["host"].State).Should().Be(("buf.apps.tt02.altinn.no", "Derived"));
        (items["process-task"].Value, items["process-task"].Source).Should().Be(("Task_1 · Utfylling · data", AltinnProcessContractEvaluator.DeployedLabel));
        (items["datatype"].Value, items["datatype"].Source).Should().Be(("model", AltinnConfigurationOverviewService.DeployedMetadata));
        items["datatype"].Detail.Should().Contain("Task Task_1").And.Contain("at most 1 per instance");
        (items["operations"].State, items["operations"].Source).Should().Be(("Resolved", AltinnAppEndpoints.DeployedSource));
        items["contract"].State.Should().Be("Current");
        items["contract"].Value.Should().Contain("app-lib 8.12.7.264");
        (items["secret"].Value, items["secret"].State).Should().Be((null, "Available"));
        items["child"].State.Should().Be("Declared");
        items["child"].Detail.Should().Contain("Declared ≠ verified");
    }

    [Fact]
    public async Task AnOldRead_IsShownAsRefreshRecommended_NotAsCurrent()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var record = harness.MessageFlows.Contract!;
        harness.MessageFlows.Contract = record with { PublicMetadata = record.PublicMetadata! with { CapturedAt = DateTimeOffset.UtcNow.AddDays(-3) } };
        var view = Contract(harness, kit);
        view.Freshness.Should().Be(AltinnContractFreshness.RefreshRecommended);
        view.FreshnessDetail.Should().Contain("refresh recommended");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<IntegrationJourneyHarness> ReadyAsync(AltinnTestKit kit, string? policySha = null)
    {
        var harness = Harness(kit);
        kit.ConfigureUnattended();
        if (policySha is not null) kit.Options.ConfirmedAppPolicies[0].PolicySha256 = policySha;
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        kit.Http.Calls.Clear();
        return harness;
    }

    private static AltinnAppContractView Contract(IntegrationJourneyHarness harness, AltinnTestKit kit) =>
        kit.Contracts(harness.MessageFlows, harness.Sources).Evaluate(Env, AltinnTestKit.AppId, harness.MessageFlows.Contract);

    private static AltinnConfigurationOverview Overview(IntegrationJourneyHarness harness, AltinnTestKit kit, AltinnTestConfiguration? config = null)
    {
        var configuration = config ?? harness.MessageFlows.Configuration;
        var contracts = kit.Contracts(harness.MessageFlows, harness.Sources);
        return new AltinnConfigurationOverviewService(harness.MessageFlows, kit.Policy, contracts)
            .Build(Env, configuration, contracts.Evaluate(Env, configuration.AppIdentifier, harness.MessageFlows.Contract), harness.MessageFlows.Contract);
    }

    private static Dictionary<string, string> Report(string reporter = AltinnTestKit.ReporterAlias, string? child = null) => new()
    {
        [SkoleNaervaerReportInput.ReporterKey] = reporter, [SkoleNaervaerReportInput.IdentitySourceKey] = "registered",
        [SkoleNaervaerAltinnForm.ChildIdentifier] = child ?? SyntheticPersonIdentifier.Generate(), [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-09-01",
        [SkoleNaervaerAltinnForm.PeriodTo] = "2026-09-30", [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
    };

    private static IntegrationJourneyRunRequest Run(IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true, Inputs = inputs.ToDictionary(),
    };

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static SkoleNaervaerAltinnExecutor Executor(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(new ServiceCollection()
        .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
        .AddTransient(_ => kit.Policy)
        .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System);

    private static IntegrationJourneyHarness Harness(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        harness.Enroll("dev:http:altinn-skolenaervaer");
        harness.Executors.Add(Executor(harness, kit));
        return harness;
    }

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
