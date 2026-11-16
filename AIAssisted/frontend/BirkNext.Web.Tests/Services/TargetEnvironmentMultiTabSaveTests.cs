using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// System Settings multi-tab safety: Target Environments live in one browser-storage entry shared by every tab, and a save used to write the whole
/// entry, so a second tab silently overwrote what the first had saved. Now a save merges per environment with what another tab stored, and the same
/// environment changed in both tabs is refused — nothing written, nobody's change lost.
/// </summary>
public sealed class TargetEnvironmentMultiTabSaveTests
{
    /// <summary>The browser storage both tabs share.</summary>
    private sealed class Storage : IJSRuntime
    {
        public readonly Dictionary<string, string?> Items = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "birkNextStorage.getItem": return ValueTask.FromResult((TValue)(object?)Items.GetValueOrDefault((string)args![0]!)!);
                case "birkNextStorage.setItem": Items[(string)args![0]!] = (string?)args[1]; return ValueTask.FromResult(default(TValue)!);
                default: return ValueTask.FromResult(default(TValue)!); // detection snapshots: empty
            }
        }
    }

    private static async Task<(FrontendAnalysisSettingsService A, FrontendAnalysisSettingsService B, Storage Js)> TwoTabs()
    {
        var js = new Storage();
        var seed = new FrontendAnalysisSettingsService();
        await seed.LoadAsync(js);
        seed.Settings.Profiles.Add(new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example.test" });
        seed.Settings.Profiles.Add(new FrontendAnalysisProfile { Id = "qa", Name = "QA", TargetUrl = "https://qa.example.test" });
        await seed.SaveAsync(js);
        var a = new FrontendAnalysisSettingsService(); await a.LoadAsync(js);
        var b = new FrontendAnalysisSettingsService(); await b.LoadAsync(js);
        return (a, b, js);
    }

    private static FrontendAnalysisProfile P(FrontendAnalysisSettingsService s, string id) => s.Settings.Profiles.Single(p => p.Id == id);

    private static async Task<FrontendAnalysisSettingsService> Reread(Storage js) { var r = new FrontendAnalysisSettingsService(); await r.LoadAsync(js); return r; }

    [Fact]
    public async Task Changes_to_different_environments_in_two_tabs_are_both_kept()
    {
        var (a, b, js) = await TwoTabs();
        P(a, "dev").TargetUrl = "https://dev2.example.test"; await a.SaveAsync(js);
        P(b, "qa").TargetUrl = "https://qa2.example.test"; await b.SaveAsync(js);

        var stored = await Reread(js);
        P(stored, "dev").TargetUrl.Should().Be("https://dev2.example.test", "tab B never touched DEV, so tab A's save survives B's");
        P(stored, "qa").TargetUrl.Should().Be("https://qa2.example.test");
    }

    [Fact]
    public async Task The_same_environment_changed_in_two_tabs_is_refused_and_nothing_is_overwritten()
    {
        var (a, b, js) = await TwoTabs();
        P(a, "dev").TargetUrl = "https://from-a.example.test"; await a.SaveAsync(js);
        P(b, "dev").TargetUrl = "https://from-b.example.test";

        var save = () => b.SaveAsync(js);
        (await save.Should().ThrowAsync<TargetEnvironmentSaveConflictException>()).Which.Environments.Should().Equal("DEV");
        P(await Reread(js), "dev").TargetUrl.Should().Be("https://from-a.example.test", "the other tab's saved change is not lost");

        await b.ReloadAsync(js);
        P(b, "dev").TargetUrl.Should().Be("https://from-a.example.test", "after Reload the tab shows what is stored");
    }

    [Fact]
    public async Task An_environment_added_in_one_tab_and_one_deleted_in_another_both_take_effect()
    {
        var (a, b, js) = await TwoTabs();
        a.Settings.Profiles.Add(new FrontendAnalysisProfile { Id = "prod", Name = "PROD" }); await a.SaveAsync(js);
        b.DeleteProfile("qa"); await b.SaveAsync(js);

        var ids = (await Reread(js)).Settings.Profiles.Select(p => p.Id).ToList();
        ids.Should().Contain(["dev", "prod"], "tab A's addition survives tab B's save").And.NotContain("qa", "tab B's deletion survives too");
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Saving_without_another_tab_behaves_as_before_and_a_second_save_merges_against_its_own_write()
    {
        var (a, _, js) = await TwoTabs();
        P(a, "dev").Name = "Development"; await a.SaveAsync(js);
        P(a, "dev").TargetUrl = "https://dev3.example.test"; await a.SaveAsync(js);

        var stored = await Reread(js);
        (P(stored, "dev").Name, P(stored, "dev").TargetUrl).Should().Be(("Development", "https://dev3.example.test"));
    }

    [Fact]
    public async Task A_local_data_reset_replaces_what_any_tab_stored()
    {
        var (a, b, js) = await TwoTabs();
        a.Settings.Profiles.Add(new FrontendAnalysisProfile { Id = "extra", Name = "Extra" }); await a.SaveAsync(js);
        await b.ResetForLocalDataResetAsync(js);

        (await Reread(js)).Settings.Profiles.Should().NotContain(p => p.Id == "extra" || p.Id == "dev", "a reset is a replacement, never a merge");
    }
}
