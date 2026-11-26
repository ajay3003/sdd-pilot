using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>System Settings → Manuell testing → Testoversikt (2026-11-25): defaults until changed, only known widgets, reset, persistence.</summary>
public sealed class ManualTestingDashboardSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-dashboard-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Defaults_apply_until_a_switch_is_saved_and_a_saved_switch_survives_a_new_store()
    {
        var store = new ManualTestingDashboardSettingsStore(_dir);
        var fresh = store.Read();
        fresh.IsOn("metric.scenarios").Should().BeTrue();
        fresh.IsOn("chart.history").Should().BeFalse("optional charts are off by default");
        fresh.IsOn("metric.testPlans").Should().BeFalse();

        store.Save(new SaveManualTestingDashboardSettingsRequest(new() { ["chart.priority"] = false, ["chart.history"] = true }), "lead", DateTimeOffset.UtcNow);
        store.Save(new SaveManualTestingDashboardSettingsRequest(new() { ["panel.translationReview"] = true }), "lead", DateTimeOffset.UtcNow);

        var reread = new ManualTestingDashboardSettingsStore(_dir).Read();   // after a restart
        reread.IsOn("chart.priority").Should().BeFalse();
        reread.IsOn("chart.history").Should().BeTrue();
        reread.IsOn("panel.translationReview").Should().BeTrue("saves merge; earlier switches are kept");
        reread.IsOn("metric.approved").Should().BeTrue("untouched widgets keep their default");
        reread.UpdatedBy.Should().Be("lead");
    }

    [Fact]
    public void Unknown_widgets_are_refused_and_nothing_is_written_and_reset_returns_to_defaults()
    {
        var store = new ManualTestingDashboardSettingsStore(_dir);
        var bad = () => store.Save(new SaveManualTestingDashboardSettingsRequest(new() { ["chart.passed"] = true }), "x", DateTimeOffset.UtcNow);
        bad.Should().Throw<ArgumentException>();
        File.Exists(Path.Combine(_dir, "dashboard-settings.json")).Should().BeFalse();

        store.Save(new SaveManualTestingDashboardSettingsRequest(new() { ["chart.priority"] = false }), "lead", DateTimeOffset.UtcNow);
        store.Save(new SaveManualTestingDashboardSettingsRequest(null, ResetToDefaults: true), "lead", DateTimeOffset.UtcNow).Visible.Should().BeEmpty();
        store.Read().IsOn("chart.priority").Should().BeTrue();
    }

    [Fact]
    public void The_registry_never_offers_execution_results()
    {
        ManualTestingDashboard.Widgets.Select(w => w.Title).Should().NotContain(t => t.Contains("Gjennomført") || t.Contains("Bestått") || t.Contains("Feilet"));
        ManualTestingDashboard.Widgets.Select(w => w.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Custom_tiles_default_to_the_E2E_tile_persist_as_a_list_and_reset_without_touching_other_widgets()
    {
        var store = new ManualTestingDashboardSettingsStore(_dir);
        store.Read().EffectiveTiles.Should().Equal(DashboardKpiTiles.Defaults);
        store.Save(new SaveManualTestingDashboardSettingsRequest(new() { ["chart.history"] = true }), "lead", DateTimeOffset.UtcNow);
        store.Save(new SaveManualTestingDashboardSettingsRequest(null)
        {
            Tiles = [new("tile.e2e", " E2E – Skolenærvær ", KpiTileDimension.TestLevel, "EndToEnd"), new("tile.nf", null, KpiTileDimension.ScenarioType, "NonFunctional", false, 1)],
        }, "lead", DateTimeOffset.UtcNow);
        store.Save(new SaveManualTestingDashboardSettingsRequest(new() { ["chart.priority"] = false }), "lead", DateTimeOffset.UtcNow);

        var reread = new ManualTestingDashboardSettingsStore(_dir).Read();
        reread.EffectiveTiles.Select(t => (t.Id, t.Title, t.Visible, t.Order)).Should().Equal(("tile.e2e", "E2E – Skolenærvær", true, 0), ("tile.nf", (string?)null, false, 1));
        reread.IsOn("chart.history").Should().BeTrue("a tile save keeps the switches, a switch save keeps the tiles");

        FluentActions.Invoking(() => store.Save(new SaveManualTestingDashboardSettingsRequest(null) { Tiles = [new("x", null, KpiTileDimension.TestLevel, "Functional")] }, "lead", DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>("Functional is not a test level");
        new ManualTestingDashboardSettingsStore(_dir).Read().EffectiveTiles.Should().HaveCount(2, "a refused save changes nothing");

        var reset = store.Save(new SaveManualTestingDashboardSettingsRequest(null) { ResetTiles = true }, "lead", DateTimeOffset.UtcNow);
        reset.EffectiveTiles.Should().Equal(DashboardKpiTiles.Defaults);
        (reset.IsOn("chart.history"), reset.IsOn("chart.priority")).Should().Be((true, false), "only the tiles are reset");
        store.Save(new SaveManualTestingDashboardSettingsRequest(null) { Tiles = [] }, "lead", DateTimeOffset.UtcNow).EffectiveTiles.Should().BeEmpty("every tile removed is kept as a choice");
    }
}
