using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// §92 immediate-save semantics of Target Environment → Integrations, consumer-mapping confirmation, contract artifacts and the
/// per-platform runtime evidence sources.
/// </summary>
public sealed class IntegrationsPaneSaveUxTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" };

    public IntegrationsPaneSaveUxTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Open() => Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));

    private static void View(IRenderedComponent<IntegrationsPane> cut, string integrationId) =>
        cut.FindAll("[data-testid=ip-row]").Single(r => r.GetAttribute("data-integration-id") == integrationId).QuerySelector("[data-testid=ip-row-view]")!.Click();

    private string SuggestedId => _api.Catalog.Integrations.First(i => i.Consumer.MappingState == ConsumerMappingState.Suggested).Id;
    private string PersonId => "dev:eventhub:birk-cdc:dbo.Person";

    [Fact]
    public void ThePaneStatesThatChangesAreSavedImmediately()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-save-note]").TextContent.Should().Contain("Integration changes are saved immediately.");
        var status = cut.Find("[data-testid=ip-save-status]");
        status.GetAttribute("role").Should().Be("status");
        status.GetAttribute("aria-live").Should().Be("polite");
        status.TextContent.Should().BeEmpty("nothing has been saved yet");
    }

    [Fact]
    public void EnableDisableIsSavedAndAnnounced()
    {
        var cut = Open();
        View(cut, PersonId);
        cut.Find("[data-testid=ip-row-toggle-enabled]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-save-status]").GetAttribute("data-state").Should().Be("Saved"));
        cut.Find("[data-testid=ip-save-status]").TextContent.Should().StartWith("Saved.").And.Contain("disabled");
        _api.Calls.Should().Contain($"enabled:{PersonId}:False");
    }

    [Fact]
    public void AFailedSaveIsStatedAndNothingClaimsSuccess()
    {
        _api.SaveFailure = new HttpRequestException("503");
        var cut = Open();
        View(cut, PersonId);
        cut.Find("[data-testid=ip-row-toggle-enabled]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-save-status]").GetAttribute("data-state").Should().Be("Failed"));
        cut.Find("[data-testid=ip-save-status]").TextContent.Should().StartWith("Save failed");
        cut.Find("[data-testid=ip-summary-enabled]").TextContent.Should().Be("16", "the failed change is not shown as applied");
    }

    [Fact]
    public void DeleteNeedsAnExplicitConfirmation()
    {
        var cut = Open();
        View(cut, PersonId);
        cut.Find("[data-testid=ip-row-delete]").Click();
        _api.Calls.Should().NotContain(c => c.StartsWith("delete:"), "the first click only asks");
        cut.Find("[data-testid=ip-delete-confirm]").TextContent.Should().Contain("Delete");
        cut.Find("[data-testid=ip-row-delete-cancel]").Click();
        cut.FindAll("[data-testid=ip-delete-confirm]").Should().BeEmpty();
        _api.Calls.Should().NotContain(c => c.StartsWith("delete:"));

        cut.Find("[data-testid=ip-row-delete]").Click();
        cut.Find("[data-testid=ip-row-delete-confirm]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-row]").Should().HaveCount(15));
        _api.Calls.Should().Contain($"delete:{PersonId}");
        cut.Find("[data-testid=ip-save-status]").TextContent.Should().Contain("deleted");
    }

    [Fact]
    public void ASuggestedConsumerIsConfirmedOnlyByThePerson()
    {
        var id = SuggestedId;
        var cut = Open();
        View(cut, id);
        _api.Saved.Should().BeEmpty("rendering a suggestion never saves it");
        cut.Find("[data-testid=ip-mapping-suggested]").TextContent.Should().Contain("Receiver rights never confirm a mapping");
        cut.Find("[data-testid=ip-mapping-confirm]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        var saved = _api.Saved.Single();
        saved.Id.Should().Be(id);
        saved.Consumer.MappingState.Should().Be(ConsumerMappingState.Confirmed);
        saved.Consumer.MappingEvidence.Should().Contain("Confirmed by a person");
        saved.Consumer.MappingConfirmedAt.Should().NotBeNull();
        saved.ConsumerGroup.Should().BeNull("confirming a consumer never invents a consumer group");
    }

    [Fact]
    public void ChangeOpensTheEditorForTheSuggestedIntegration()
    {
        var cut = Open();
        View(cut, SuggestedId);
        cut.Find("[data-testid=ip-mapping-change]").Click();
        cut.FindComponent<IntegrationDefinitionEditor>().Instance.Original!.Id.Should().Be(SuggestedId);
        _api.Saved.Should().BeEmpty();
    }

    [Fact]
    public void ContractsAreUploadedPerRoleAndCanBeRemoved()
    {
        var cut = Open();
        View(cut, PersonId);
        var contracts = cut.FindAll("[data-testid=ip-contract]");
        contracts.Select(c => c.GetAttribute("data-role")).Should().Equal("Producer", "Consumer");
        contracts.Should().OnlyContain(c => c.GetAttribute("data-configured") == "false");

        cut.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromText("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"id":{"type":"integer"}}}""", "person-producer.json"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-contract]")[0].GetAttribute("data-configured").Should().Be("true"));
        _api.Calls.Should().Contain($"contract:{PersonId}:Producer");
        cut.Find("[data-testid=ip-contract-file]").TextContent.Should().Be("person-producer.json");
        cut.Find("[data-testid=ip-save-status]").GetAttribute("data-state").Should().Be("Saved");

        cut.Find("[data-testid=ip-contract-remove]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-contract]")[0].GetAttribute("data-configured").Should().Be("false"));
        _api.Calls.Should().Contain($"contract-remove:{PersonId}:Producer");
    }

    [Fact]
    public void ARejectedContractIsExplainedAndNotStored()
    {
        _api.ContractRejection = "Sample JSON is not a JSON Schema.";
        var cut = Open();
        View(cut, PersonId);
        cut.FindComponents<InputFile>()[1].UploadFiles(InputFileContent.CreateFromText("""{"id":1}""", "sample.json"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-contract-error]").TextContent.Should().Contain("not a JSON Schema"));
        cut.Find("[data-testid=ip-contract-error]").GetAttribute("role").Should().Be("alert");
        cut.Find("[data-testid=ip-save-status]").GetAttribute("data-state").Should().Be("Failed");
        _api.Contracts.Should().BeEmpty();
    }

    [Fact]
    public void RuntimeEvidenceSourcesAreEditedAsIdentifiersAndASasUrlIsRejected()
    {
        var cut = Open();
        cut.Find("[data-testid^=ip-runtime-] > button").Click();
        cut.Find("[data-testid=ip-runtime-summary]").TextContent.Should().Contain("Not configured").And.Contain("no lag threshold");
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.Find("[data-testid=ip-runtime-checkpoint]").Change("https://acct.blob.core.windows.net/checkpoints?sv=2024&sig=abc");
        cut.Find("[data-testid=ip-runtime-save]").Click();
        cut.Find("[data-testid=ip-runtime-error]").TextContent.Should().Contain("never a SAS URL");
        _api.SavedPlatforms.Should().BeEmpty();

        cut.Find("[data-testid=ip-runtime-checkpoint]").Change("https://acct.blob.core.windows.net/checkpoints");
        cut.Find("[data-testid=ip-runtime-metadata]").Change(true);
        cut.Find("[data-testid=ip-runtime-window]").Change("12");
        cut.Find("[data-testid=ip-runtime-save]").Click();
        cut.WaitForAssertion(() => _api.SavedPlatforms.Should().ContainSingle());
        var settings = _api.SavedPlatforms.Single().RuntimeEvidence!;
        settings.EventHubMetadata.Should().BeTrue();
        settings.CheckpointContainerUrl.Should().Be("https://acct.blob.core.windows.net/checkpoints");
        settings.ReviewWindowHours.Should().Be(12);
        settings.MaxConsumerLagEvents.Should().BeNull("no threshold is invented");
        settings.MaxCheckpointAgeMinutes.Should().BeNull();
    }

    [Fact]
    public void TheConsumerGroupHelperNeverAssumesDefault()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-add]").Click();
        var input = cut.Find("[data-testid=ip-edit-consumer-group]");
        input.GetAttribute("aria-describedby").Should().Be("ip-edit-consumer-group-help");
        input.GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("#ip-edit-consumer-group-help").TextContent.Should().Contain("Consumer group is required for checkpoint/lag review").And.Contain("never assumed");
    }
}
