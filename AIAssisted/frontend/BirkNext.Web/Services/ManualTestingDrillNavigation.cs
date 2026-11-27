using BirkNext.Web.Services.Explorers;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Drill-down from Testoversikt (2026-11-26) into Finn og forstå with the exact selection behind a chart bar: the module and specification of the
/// scenarios are chosen in the shared selection (the same one the picker sets), and the scenario identities are handed over as the page's filter.
/// When the specification cannot be chosen, nothing is opened and the reason is returned — never a general page without the right selection.
/// </summary>
public static class ManualTestingDrillNavigation
{
    /// <summary>The overview's name for a specification at the root of the project; the explorers call that module <see cref="ArtifactModules.ProjectLevel"/>.</summary>
    public const string ProjectLevelModule = BirkNext.TestPlanning.ModuleStructure.ProjectWide;

    /// <summary>The explorers' module key for a Testoversikt module: both groups outside the modules (the project's own specifications and
    /// documents outside every module folder) are the explorers' project level.</summary>
    public static string ExplorerModule(string module) =>
        module is BirkNext.TestPlanning.ModuleStructure.ProjectWide or BirkNext.TestPlanning.ModuleStructure.Unassigned ? ArtifactModules.ProjectLevel : module;

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Chooses <paramref name="module"/> and the specification at <paramref name="specificationPath"/> in the shared selection; null when done,
    /// else why it could not be chosen.</summary>
    public static async Task<string?> SelectSpecificationAsync(IServiceProvider services, string module, string specificationPath)
    {
        if (services.GetService(typeof(IArtifactExplorerContext)) is not IArtifactExplorerContext explorer) return "Valget av modul og spesifikasjon er ikke tilgjengelig her.";
        try
        {
            var moduleKey = ExplorerModule(module);
            var state = await explorer.GetStateAsync(WorkspaceArtifactType.Specification);
            if (!string.Equals(state.Module ?? "", moduleKey, StringComparison.Ordinal))
            {
                await explorer.SelectModuleAsync(moduleKey);
                state = await explorer.GetStateAsync(WorkspaceArtifactType.Specification);
            }
            if (!SamePath(state.Selected?.SourcePath, specificationPath))
            {
                if (state.Artifacts.FirstOrDefault(a => SamePath(a.SourcePath, specificationPath)) is not { } wanted)
                    return $"Spesifikasjonen «{specificationPath}» finnes ikke i modulen «{module}» i prosjektets dokumenter.";
                explorer.Select(WorkspaceArtifactType.Specification, wanted.Id, state.Module);
                state = await explorer.GetStateAsync(WorkspaceArtifactType.Specification);
                if (!SamePath(state.Selected?.SourcePath, specificationPath)) return $"Spesifikasjonen «{wanted.DisplayName}» kunne ikke velges.";
            }
            // Requirements are read from the review context: rebuilt from the new choice, as the picker does; the choice is saved like the picker's.
            if (services.GetService(typeof(IReviewContextProvider)) is IReviewContextProvider context) await context.RebuildAsync();
            if (services.GetService(typeof(IWorkspaceAutoSaveService)) is IWorkspaceAutoSaveService autoSave) _ = autoSave.FlushAsync();
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return "Modul og spesifikasjon kunne ikke velges: " + e.Message; }
    }

    /// <summary>
    /// Opens Finn og forstå on <paramref name="specificationPath"/> showing exactly <paramref name="identities"/> (named <paramref name="label"/>).
    /// Returns null when opened, else why not (then nothing changed on the page).
    /// </summary>
    public static async Task<string?> OpenScenariosAsync(IServiceProvider services, IJSRuntime js, NavigationManager nav, string projectKey, string module,
        string specificationPath, IReadOnlyCollection<string> identities, string label)
    {
        if (identities.Count == 0) return "Utvalget er tomt.";
        if (await SelectSpecificationAsync(services, module, specificationPath) is { } refused) return refused;
        await TabDraftStore.WriteAsync(js, TabDraftStore.DiscoverFilterKey(projectKey), ScenarioBrowserPresentation.FromOverview(identities, label));
        // The open scenario of an earlier visit is not part of this selection: the first scenario of the selection opens.
        await TabDraftStore.WriteAsync(js, TabDraftStore.DiscoverContextKey(projectKey), new ScenarioWorkspacePresentation.WorkContext(null, null));
        nav.NavigateTo(ManualTestingPresentation.DiscoverRoute);
        return null;
    }

    /// <summary>
    /// A test-case figure of Testoversikt (V12, 2026-11-27): Planlegg og klargjør opens on the first plan of the population with exactly these cases
    /// (all plans named there); the shared scope is unchanged. Nothing opens for an empty population.
    /// </summary>
    public static async Task<string?> OpenCasesAsync(IJSRuntime js, NavigationManager nav, string projectKey, string label, IReadOnlyList<PlanPreparePresentation.CaseRef> cases)
    {
        if (cases.Count == 0) return "Ingen testtilfeller i utvalget.";
        await TabDraftStore.WriteAsync(js, TabDraftStore.PlanCaseSelectionKey(projectKey), new PlanPreparePresentation.CaseSelection(label, [.. cases]));
        nav.NavigateTo($"{ManualTestingPresentation.PlanRoute}?draft={cases[0].PlanId}");
        return null;
    }

    /// <summary>A KPI tile's click-through (2026-11-26): Finn og forstå with exactly this filter, keeping the active project/module/specification
    /// selection (the scope is not changed) and opening no earlier scenario.</summary>
    public static async Task OpenFilteredAsync(IJSRuntime js, NavigationManager nav, string projectKey, ScenarioBrowserPresentation.Filter filter)
    {
        await TabDraftStore.WriteAsync(js, TabDraftStore.DiscoverFilterKey(projectKey), filter);
        await TabDraftStore.WriteAsync(js, TabDraftStore.DiscoverContextKey(projectKey), new ScenarioWorkspacePresentation.WorkContext(null, null));
        nav.NavigateTo(ManualTestingPresentation.DiscoverRoute);
    }

    /// <summary>
    /// Opens Finn og forstå on <paramref name="specificationPath"/> with one user story (<paramref name="storyKey"/>, its canonical key) or every story
    /// of one implementation status (<paramref name="status"/>, the same engine's status the page shows). Returns null when opened, else why not.
    /// </summary>
    public static async Task<string?> OpenStoriesAsync(IServiceProvider services, IJSRuntime js, NavigationManager nav, string projectKey, string module,
        string specificationPath, string? storyKey, BirkNext.TestPlanning.UserStoryTaskStatus? status)
    {
        if (await SelectSpecificationAsync(services, module, specificationPath) is { } refused) return refused;
        var filter = new ScenarioBrowserPresentation.Filter(All: true, Story: storyKey, ImplementationStatus: storyKey is null ? status?.ToString() : null);
        await TabDraftStore.WriteAsync(js, TabDraftStore.DiscoverFilterKey(projectKey), filter);
        await TabDraftStore.WriteAsync(js, TabDraftStore.DiscoverContextKey(projectKey), new ScenarioWorkspacePresentation.WorkContext(storyKey, null));
        nav.NavigateTo(ManualTestingPresentation.DiscoverRoute);
        return null;
    }
}
