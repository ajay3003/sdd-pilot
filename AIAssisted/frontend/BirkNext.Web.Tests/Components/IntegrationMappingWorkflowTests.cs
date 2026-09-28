using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class IntegrationMappingWorkflowTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development };
    private const string Tiltak = "dev:eventhub:birk-cdc:dbo.Tiltak";
    private readonly IntegrationMappingEvidenceSession _session = new();
    public IntegrationMappingWorkflowTests() { Services.AddSingleton<IIntegrationCatalogApiService>(_api); Services.AddSingleton(_session); JSInterop.Mode = JSRuntimeMode.Loose; }
    private IRenderedComponent<IntegrationsPane> Open(string? focus = null) => Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile).Add(c => c.Focus, focus));
    private void Returns(IntegrationMappingEvidenceState state, params IntegrationMappingEvidenceItem[] checks) =>
        _api.MappingCheck = id => Task.FromResult(new IntegrationMappingEvidenceCheck
        {
            IntegrationId = id, Topic = "m2lb-cdc-dev.BirkM2LB.dbo.Tiltak", SuggestedConsumer = "Tjeneste API", CompletedAt = DateTimeOffset.UtcNow, OverallState = state, Checks = [.. checks],
            ManualFollowUp = ["Confirm the mapping only if the consumer relationship is known from a trusted source."],
        });
    private static IntegrationMappingEvidenceItem Found(string label, string summary) => new() { Label = label, Summary = summary, State = IntegrationEvidenceState.Available, Freshness = IntegrationEvidenceItemFreshness.Current };
    private static IntegrationMappingEvidenceItem Missing(string label, IntegrationEvidenceState state, string reason) => new() { Label = label, State = state, MissingReason = reason, Summary = reason };
    private static AngleSharp.Dom.IElement Row(IRenderedComponent<IntegrationsPane> cut) => cut.FindAll("[data-testid=ip-row]").Single(r => r.GetAttribute("data-integration-id") == Tiltak);

    [Fact]
    public void FourPrimaryCardsAndActionableConfirmationFilter()
    {
        var cut = Open();
        cut.FindAll(".ip-summary > div").Should().HaveCount(4);
        cut.Find("[data-testid=ip-summary-runtime]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid=ip-review-mappings]").TextContent.Should().Be("Review 15 mappings");
        cut.Find("[data-testid=ip-review-mappings]").Click();
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(15);
        cut.Find("[data-testid=ip-filter-state]").GetAttribute("value").Should().Be(nameof(IntegrationConfigurationState.NeedsConfirmation));
        cut.Find("[data-testid=ip-active-filter]").TextContent.Should().Contain("Needs confirmation");
        cut.Find("[data-testid=ip-clear-filters]").Click();
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16);
    }

    [Fact]
    public void PlayHasContextAndUnknownConsumerIsNotDuplicated()
    {
        var cut = Open();
        var play = Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!;
        play.GetAttribute("aria-label").Should().Contain("Tiltak").And.Contain("Tjeneste API");
        play.GetAttribute("title").Should().Be("Test mapping");
        cut.FindAll("[data-testid=ip-test-mapping]").Should().HaveCount(15);
        cut.FindAll("[data-mapping=NeedsConfirmation]").Should().OnlyContain(c => c.TextContent.Contains("Not assigned") && !c.TextContent.Contains("Needs confirmation"));
        cut.Find("[data-testid=ip-technical]").QuerySelectorAll("[data-testid=ip-test-mapping]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(IntegrationMappingEvidenceState.NotTestable, "Not testable")]
    [InlineData(IntegrationMappingEvidenceState.PartialEvidence, "Partial evidence")]
    [InlineData(IntegrationMappingEvidenceState.StrongEvidence, "Strong evidence")]
    [InlineData(IntegrationMappingEvidenceState.NoSupportingEvidence, "No supporting evidence")]
    public void EvidenceNeverConfirms_ExplicitConfirmationUpdatesCountsAndProvenance(IntegrationMappingEvidenceState state, string label)
    {
        _api.MappingCheck = id => Task.FromResult(new IntegrationMappingEvidenceCheck
        {
            IntegrationId = id, Topic = "topic", SuggestedConsumer = "Tjeneste API", CompletedAt = DateTimeOffset.UtcNow, OverallState = state,
            Checks = [new() { Label = "Consumer configuration", State = IntegrationEvidenceState.Available, Summary = "Suggested consumer is configured" },
                new() { Label = "Checkpoint", State = IntegrationEvidenceState.NotConfigured, MissingReason = "Checkpoint evidence source not configured" }],
            ManualFollowUp = ["Confirm only from a trusted source"],
        });
        var cut = Open();
        Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-mapping-result]").TextContent.Should().Be(label));
        cut.Find("[data-testid=mapping-found]").TextContent.Should().Contain("Suggested consumer");
        cut.Find("[data-testid=mapping-missing]").TextContent.Should().Contain("Not configured").And.Contain("Checkpoint");
        _api.Saved.Should().BeEmpty();
        Row(cut).GetAttribute("data-configuration").Should().Be("NeedsConfirmation");
        Row(cut).QuerySelector("[data-testid=ip-row-consumer]")!.TextContent.Should().Contain("Suggested");
        Row(cut).QuerySelector("[data-testid=ip-inline-confirm]")!.Click();
        cut.WaitForAssertion(() => Row(cut).GetAttribute("data-configuration").Should().Be("Ready"));
        Row(cut).QuerySelector("[data-testid=ip-row-consumer]")!.TextContent.Should().Contain("Confirmed");
        _api.Saved.Single().Consumer.MappingConfirmedAt.Should().NotBeNull();
        _api.Saved.Single().Consumer.MappingEvidence.Should().Contain("suggested by:");
        cut.Find("[data-testid=ip-summary-ready]").TextContent.Should().Be("2");
        cut.Find("[data-testid=ip-summary-confirm]").TextContent.Should().Be("14");
    }

    [Fact]
    public async Task DuplicateExecutionIsDisabledWhileOtherRowsRemainUsable()
    {
        var completion = new TaskCompletionSource<IntegrationMappingEvidenceCheck>();
        _api.MappingCheck = _ => completion.Task;
        var cut = Open();
        var pending = Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!.ClickAsync(new());
        cut.WaitForAssertion(() => Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!.HasAttribute("disabled").Should().BeTrue());
        Row(cut).TextContent.Should().Contain("Testing mapping");
        cut.FindAll("[data-testid=ip-row-view]").Should().OnlyContain(b => !b.HasAttribute("disabled"));
        completion.SetResult(new() { IntegrationId = Tiltak, OverallState = IntegrationMappingEvidenceState.NotTestable });
        await pending;
        _api.Calls.Count(c => c == "mapping-evidence:" + Tiltak).Should().Be(1);
    }

    [Fact]
    public void RuntimeCtaOpensExistingEditorAndIqrRemainsAvailable()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-configure-runtime]").Click();
        cut.Find("[data-testid=ip-runtime-form]");
        cut.Find("[data-testid^=ip-runtime-].disclosure button").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=ip-open-iqr]").GetAttribute("href").Should().Be("/integration-quality-review");
        _api.SavedPlatforms.Should().BeEmpty();
    }

    [Fact]
    public void DefaultSortPrioritizesActionAndPersonRemainsReadyButPartial()
    {
        var rows = IntegrationsPanePresentation.Groups(_api.Catalog).SelectMany(g => g.Rows).ToList();
        rows.Take(15).Should().OnlyContain(r => r.Configuration == IntegrationConfigurationState.NeedsConfirmation);
        rows.Last().ShortName.Should().Be("Person");
        rows.Last().ReviewReadiness.Should().Be("Partial");
        rows.Last().ReviewReadinessDetail.Should().Contain("runtime telemetry");
        var changed = _api.Catalog with { Integrations = [.. _api.Catalog.Integrations, M2lbFixture.Topic("Missing") with { Id = "missing", EndpointOrTopic = null }, M2lbFixture.Topic("Disabled") with { Id = "disabled", Enabled = false }] };
        var sorted = IntegrationsPanePresentation.Groups(changed).SelectMany(g => g.Rows).ToList();
        sorted.First().Configuration.Should().Be(IntegrationConfigurationState.NeedsConfiguration);
        sorted.Last().Configuration.Should().Be(IntegrationConfigurationState.Disabled);
    }

    [Fact]
    public void ConfirmationInFilteredViewRemovesTheRowImmediately()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-review-mappings]").Click();
        Row(cut).QuerySelector("[data-testid=ip-inline-confirm]")!.Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-row]").Should().HaveCount(14));
        cut.Find("[data-testid=ip-review-mappings]").TextContent.Should().Be("Review 14 mappings");
    }

    [Fact]
    public void PlayActionNamesTheSuggestedMappingItTests()
    {
        var play = Row(Open()).QuerySelector("[data-testid=ip-test-mapping]")!;
        play.GetAttribute("aria-label").Should().Be("Test suggested mapping for Tiltak and Tjeneste API");
        play.TagName.Should().Be("BUTTON");
        Row(Open()).TextContent.Should().Contain("BirkNext has a suggested consumer mapping, but the relationship has not yet been verified.");
    }

    [Fact]
    public void NotTestableExplainsMissingSourcesOffersConfigurationAndLeavesTheMappingSuggested()
    {
        const string disabled = "Azure runtime evidence is disabled for this BirkNext instance.";
        Returns(IntegrationMappingEvidenceState.NotTestable,
            Found("Consumer configuration", "Tjeneste API is configured (Suggested). Configuration is not observed processing."),
            Missing("Event Hub metadata", IntegrationEvidenceState.NotConfigured, disabled),
            Missing("Consumer-group evidence", IntegrationEvidenceState.NotConfigured, disabled),
            Missing("Checkpoint evidence", IntegrationEvidenceState.NotConfigured, disabled),
            Missing("Application Insights evidence", IntegrationEvidenceState.NotConfigured, disabled));
        var cut = Open();
        Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mapping-state]").TextContent.Should().Be("Not testable"));
        cut.Find("[data-testid=mapping-found]").TextContent.Should().Contain("Tjeneste API is configured");
        var missing = cut.FindAll("[data-testid=mapping-missing] li");
        missing.Should().HaveCount(4).And.OnlyContain(li => li.TextContent.Contains("Not configured"));
        cut.Find("[data-testid=mapping-meaning]").TextContent.Should().Contain("not a failure");
        cut.Find("[data-testid=mapping-configure]").Click();
        cut.Find("[data-testid=ip-runtime-form]");
        Row(cut).QuerySelector("[data-testid=ip-row-consumer]")!.TextContent.Should().Contain("Suggested");
        Row(cut).GetAttribute("data-configuration").Should().Be("NeedsConfirmation");
        _api.Saved.Should().BeEmpty();
        _api.SavedPlatforms.Should().BeEmpty("opening the runtime editor saves nothing");
        _session.For("dev")[Tiltak].OverallState.Should().Be(IntegrationMappingEvidenceState.NotTestable);
    }

    [Fact]
    public void PartialEvidenceShowsFoundMissingAndFollowUpWithoutConfirming()
    {
        Returns(IntegrationMappingEvidenceState.PartialEvidence,
            Found("Event Hub metadata", "Topic exists; 1 partitions observed."),
            Missing("Direct topic to consumer relationship", IntegrationEvidenceState.NotSupported, "No direct relationship evidence."),
            Missing("Checkpoint evidence", IntegrationEvidenceState.NotConfigured, "Consumer group not configured; checkpoints cannot be attributed."));
        var cut = Open();
        Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-mapping-result]").TextContent.Should().Be("Partial evidence"));
        cut.Find("[data-testid=mapping-found]").TextContent.Should().Contain("Topic exists");
        cut.Find("[data-testid=mapping-missing]").TextContent.Should().Contain("Not supported").And.Contain("No direct relationship evidence");
        cut.Find("[data-testid=mapping-follow-up]").TextContent.Should().Contain("trusted source");
        cut.Find("[data-testid=mapping-provenance]").TextContent.Should().Be("Suggested");
        cut.Find("[data-testid=mapping-technical] .disclosure-body").HasAttribute("hidden").Should().BeTrue();
        _api.Saved.Should().BeEmpty();
    }

    [Fact]
    public void StrongEvidenceStaysSuggestedUntilExplicitConfirmFromThePanel()
    {
        Returns(IntegrationMappingEvidenceState.StrongEvidence, Found("Topic subscription", "Exact subscription observed."));
        var cut = Open();
        Row(cut).QuerySelector("[data-testid=ip-test-mapping]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mapping-state]").TextContent.Should().Be("Strong evidence"));
        cut.Find("[data-testid=mapping-provenance]").TextContent.Should().Be("Suggested");
        Row(cut).GetAttribute("data-configuration").Should().Be("NeedsConfirmation");
        _api.Saved.Should().BeEmpty();
        cut.Find("[data-testid=mapping-confirm]").Click();
        cut.WaitForAssertion(() => Row(cut).GetAttribute("data-configuration").Should().Be("Ready"));
        Row(cut).QuerySelector("[data-testid=ip-row-consumer]")!.TextContent.Should().Contain("Confirmed");
        _api.Saved.Should().ContainSingle().Which.Consumer.MappingState.Should().Be(ConsumerMappingState.Confirmed);
    }

    [Fact]
    public void ReviewReadinessFilterUsesExistingStatesAndClears()
    {
        var cut = Open();
        var options = cut.FindAll("[data-testid=ip-filter-readiness] option").Select(o => o.TextContent).ToList();
        options.Should().Equal("All", "Partial (16)");
        cut.Find("[data-testid=ip-filter-readiness]").Change("Partial");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16);
        cut.Find("[data-testid=ip-active-filter]").TextContent.Should().Contain("Review readiness: Partial");
        cut.Find("[data-testid=ip-row-readiness]").TextContent.Should().Contain("Configuration is usable, but some runtime, checkpoint, consumer or contract evidence is unavailable.");
        cut.Find("[data-testid=ip-clear-filters]").Click();
        cut.FindAll("[data-testid=ip-active-filter]").Should().BeEmpty();
    }

    [Fact]
    public void BadgesCarryReadableText()
    {
        _api.Catalog = _api.Catalog with { Integrations = [.. _api.Catalog.Integrations, M2lbFixture.Topic("Missing") with { Id = "missing", EndpointOrTopic = null }, M2lbFixture.Topic("Off") with { Id = "off", Enabled = false }] };
        var cut = Open();
        var configuration = cut.FindAll("[data-testid=ip-row-configuration]").Select(b => b.TextContent.Trim()).Distinct().ToList();
        configuration.Should().Contain(["Ready", "Needs confirmation", "Needs configuration", "Disabled"]);
        cut.FindAll("[data-testid=ip-row-consumer] .ip-pill").Select(b => b.TextContent.Trim()).Distinct().Should().BeEquivalentTo(["Suggested", "Confirmed"]);
        cut.FindAll("[data-testid=ip-row-readiness] .ip-pill").Select(b => b.TextContent.Trim()).Should().Contain("Partial");
        cut.FindAll("[data-testid=ip-row-consumer]").Should().NotContain(c => c.TextContent.Contains("(suggested)"));
    }

    [Fact]
    public void DeepLinkFocusMappingsFiltersToNeedsConfirmation()
    {
        var cut = Open(IntegrationReviewPrerunPresentation.FocusMappings);
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(15);
        cut.Find("[data-testid=ip-active-filter]").TextContent.Should().Contain("Needs confirmation");
    }

    [Fact]
    public void DeepLinkFocusRuntimeOpensTheExistingRuntimeEditor()
    {
        var cut = Open(IntegrationReviewPrerunPresentation.FocusRuntime);
        cut.Find("[data-testid=ip-runtime-form]");
        _api.SavedPlatforms.Should().BeEmpty();
    }

    [Fact]
    public void DeepLinkFocusContractsOpensAnIntegrationsContractArea()
    {
        var cut = Open(IntegrationReviewPrerunPresentation.FocusContracts);
        cut.Find("[data-testid=ip-row-detail] [data-testid=ip-contracts]").GetAttribute("tabindex").Should().Be("-1");
        _api.Saved.Should().BeEmpty();
    }

    [Fact]
    public void TechnicalTopicsAreCollapsedAndExcludedFromBusinessCounts()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-technical] .disclosure-body").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=ip-technical]").TextContent.Should().Contain("excluded from IQR business-integration counts");
        cut.Find("[data-testid=ip-summary-configured]").TextContent.Should().Be("16");
    }
}
