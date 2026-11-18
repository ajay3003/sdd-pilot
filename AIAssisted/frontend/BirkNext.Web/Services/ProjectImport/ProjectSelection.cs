using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>One imported project the tester can work on: an application (its packages grouped) or a legacy import without one.</summary>
/// <param name="Value">Stable option value: <c>app:{applicationId}</c>, <c>import:{importId}</c> or <c>sample:{slug}</c>.</param>
/// <param name="Label">What the tester reads; names that occur twice carry when they were last imported so the options stay distinguishable.</param>
public sealed record ProjectChoice(string Value, string Name, string Label, int Packages, DateTimeOffset? LastImportedAt);

public enum ProjectSwitchOutcome { Unchanged, Switched, SwitchedNotSaved, Unavailable }

/// <summary>
/// The one project-selection mechanism every workspace's project selector uses (2026-11-18). It reads the authoritative imported-project
/// registry — the workspace lifecycle's applications and their Project Import records — and switches through
/// <see cref="ProjectImportActivation"/>, exactly as Project Import's "Show this package" does; it never creates a project, never lists folders
/// and keeps no selection of its own: <see cref="IArtifactExplorerContext.CurrentScope"/> stays the single answer every page reads, and the
/// workspace auto-save persists the choice (server-side workspace state, restored at start-up).
/// </summary>
public sealed class ProjectSelection(WorkspaceArtifactRepository repository, ProjectImportActivation activation, IServiceProvider services)
{
    public const string ApplicationPrefix = "app:", ImportPrefix = "import:", SamplePrefix = "sample:";

    /// <summary>The imported projects, by name. A selected Sample Project (not an import) is listed first so the control never lies about what
    /// the workspace shows; other Sample Projects are chosen on the Sample Projects page.</summary>
    public IReadOnlyList<ProjectChoice> Choices
    {
        get
        {
            var lifecycle = repository.SddLifecycle;
            var choices = new List<ProjectChoice>();
            foreach (var application in lifecycle.Applications ?? [])
            {
                var imports = ApplicationContexts.Imports(lifecycle, application.Id);
                if (imports.Count == 0) continue;
                var name = string.IsNullOrWhiteSpace(application.Name) ? ApplicationContexts.NameOf(imports).Name : application.Name;
                choices.Add(new(ApplicationPrefix + application.Id, name, name, imports.Count, imports.Max(i => i.ImportedAt)));
            }
            foreach (var record in lifecycle.ProjectImports.Where(i => ApplicationContexts.Find(lifecycle, i.ApplicationId) is null))
            {
                var name = string.IsNullOrWhiteSpace(record.ProjectName) ? record.ArchiveFileName : record.ProjectName;
                choices.Add(new(ImportPrefix + record.ImportId, name, name, 1, record.ImportedAt));
            }
            var duplicates = choices.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = choices
                .Select(c => duplicates.Contains(c.Name) && c.LastImportedAt is { } at ? c with { Label = $"{c.Name} — imported {at.ToLocalTime():yyyy-MM-dd HH:mm}" } : c)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(c => c.LastImportedAt).ToList();
            if (SampleProject is { } sample) ordered.Insert(0, new(SamplePrefix + sample, sample, $"{sample} (Sample Project)", 0, null));
            return ordered;
        }
    }

    /// <summary>The option that names what the workspace works on now; empty when no project is selected.</summary>
    public string CurrentValue
    {
        get
        {
            if (SampleProject is { } sample) return SamplePrefix + sample;
            var lifecycle = repository.SddLifecycle;
            if (ApplicationContexts.Current(lifecycle) is { } application && ApplicationContexts.Imports(lifecycle, application.Id).Count > 0)
                return ApplicationPrefix + application.Id;
            if (activation.Current is { } current)
                return ApplicationContexts.Find(lifecycle, current.ApplicationId) is { } owner ? ApplicationPrefix + owner.Id : ImportPrefix + current.ImportId;
            return "";
        }
    }

    /// <summary>Raised after the tester switched project here; the layout re-creates the page so every module reloads for the new project.</summary>
    public event Action? Switched;

    /// <summary>Increases with every switch made here (the layout keys the page content on it).</summary>
    public int Generation { get; private set; }

    /// <summary>A message about the last switch for the next selector instance to show once (the page is re-created by the switch).</summary>
    public string? Notice { get; set; }

    /// <summary>True when at least one project can be chosen.</summary>
    public bool HasProjects => Choices.Count > 0;

    private string? SampleProject => string.IsNullOrWhiteSpace(repository.ProjectName) ? null : repository.ProjectName;

    /// <summary>
    /// Works on the chosen project: an application shows its primary documents (as after an import); a legacy import is shown as itself. A
    /// single-source Explorer choice of another project is cleared, because it would otherwise keep every page on the previous project.
    /// The choice is saved before returning; nothing is uploaded, re-imported, analysed or re-bound.
    /// </summary>
    public async Task<ProjectSwitchOutcome> SwitchAsync(string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value) || value == CurrentValue) return ProjectSwitchOutcome.Unchanged;
        var lifecycle = repository.SddLifecycle;
        var before = (lifecycle.CurrentProjectImportId, lifecycle.CurrentApplicationId, repository.ProjectName);
        if (value.StartsWith(ApplicationPrefix, StringComparison.Ordinal))
        {
            var applicationId = value[ApplicationPrefix.Length..];
            if (ApplicationContexts.Imports(lifecycle, applicationId) is not { Count: > 0 } imports) return ProjectSwitchOutcome.Unavailable;
            if (!activation.UseApplication(applicationId, await SourceSetupAsync(cancellationToken)))
            {
                activation.UseAsCurrent(imports[^1].ImportId);
                lifecycle.CurrentApplicationId = applicationId;
            }
        }
        else if (value.StartsWith(ImportPrefix, StringComparison.Ordinal))
        {
            if (!activation.UseAsCurrent(value[ImportPrefix.Length..])) return ProjectSwitchOutcome.Unavailable;
        }
        else return ProjectSwitchOutcome.Unavailable;

        if (services.GetService<IArtifactExplorerContext>() is { ExplorerSource: { } source } explorer
            && lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == source)?.ApplicationId is var sourceApplication
            && (sourceApplication is null || sourceApplication != lifecycle.CurrentApplicationId))
            explorer.SelectSource(null);
        // UseAsCurrent is silent when the package was already shown; the application may still have changed (or the Sample Project been left).
        if (before.CurrentProjectImportId == lifecycle.CurrentProjectImportId) repository.NotifyArtifactsChanged();

        var outcome = services.GetService<IWorkspaceAutoSaveService>() is { } autoSave && !await autoSave.SaveNowAsync()
            ? ProjectSwitchOutcome.SwitchedNotSaved
            : ProjectSwitchOutcome.Switched;
        Generation++;
        Switched?.Invoke();
        return outcome;
    }

    /// <summary>Source setup classifies an application's packages (which one is the primary application source). Read-only; failure → null.</summary>
    private async Task<SourceSetupView?> SourceSetupAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IIntegrationJourneysApiService>() is not { } journeys) return null;
        var environment = services.GetService<IFrontendAnalysisSettingsService>()?.ActiveProfile?.Id;
        try { return (await journeys.SourceSetupAsync(environment, cancellationToken)).Value; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { return null; }
    }
}

/// <summary>
/// Pages with unsaved work register what would be lost; a project switch asks before discarding it (the same protection as their
/// NavigationLock). One registration per page instance; dispose it when the page goes.
/// </summary>
public sealed class UnsavedWorkGuard
{
    private readonly List<Func<string?>> _sources = [];

    public IDisposable Register(Func<string?> unsavedDescription)
    {
        _sources.Add(unsavedDescription);
        return new Registration(() => _sources.Remove(unsavedDescription));
    }

    /// <summary>The first registered description of unsaved work (e.g. "This plan has unsaved changes."), or null when nothing would be lost.</summary>
    public string? Pending => _sources.Select(s => s()).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
