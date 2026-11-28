using BirkNext.TestPlanning;
using O = BirkNext.Web.Services.ManualTestingOverviewPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// The shared source scope (Technical Testing V2, 2026-11-28): the module / specification / document chosen in the shared scope picker
/// (<c>SpecificationScopePicker</c>, the explorers' selection), read exactly as Testoversikt reads it — so Manual and Technical Testing always show
/// the same scope. The level is derived from the choice (never a second state); null module = all modules; the empty string = project level.
/// </summary>
public static class TestScopeReader
{
    public sealed record Result(O.Scope Scope, string? Error);

    public static async Task<Result> ReadAsync(IServiceProvider services)
    {
        if (services.GetService(typeof(Explorers.IArtifactExplorerContext)) is not Explorers.IArtifactExplorerContext explorer) return new(O.Scope.Project, null);
        try
        {
            var state = await explorer.GetStateAsync(WorkspaceArtifactType.Specification);
            if (state.Status == Explorers.ExplorerArtifactStatus.Error) return new(O.Scope.Project, state.Error ?? "Kildedokumentet er ikke tilgjengelig.");
            var module = state.Module;
            var pack = state.Pack;
            var document = state.Status == Explorers.ExplorerArtifactStatus.Loaded ? state.Selected?.SourcePath : null;
            var scope = document is not null ? new O.Scope(O.Level.Specification, module, document, pack)
                : pack is not null ? new O.Scope(O.Level.Specification, module, null, pack)
                : module is not null ? new O.Scope(O.Level.Module, module, null)
                : O.Scope.Project;
            return new(scope, null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new(O.Scope.Project, e.Message); }
    }

    /// <summary>A scope in words (for headings and the drilldown title).</summary>
    public static string Describe(O.Scope scope) => scope.Level switch
    {
        O.Level.Module => $"Modul {(string.IsNullOrEmpty(scope.Module) ? ModuleStructure.ProjectWide : scope.Module)}",
        O.Level.Specification when scope.SpecificationPath is { } doc => $"Dokument {doc}",
        O.Level.Specification => $"Spesifikasjon {scope.SpecificationPack}",
        _ => "Hele prosjektet",
    };
}
