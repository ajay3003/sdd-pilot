namespace BirkNext.Web.Services;

public class FeatureVisibilityService
{
    private FeatureVisibilityDto _flags = new();

    public bool IsLoaded { get; private set; }

    // Called once from MainLayout on startup.
    // AdminApiService is passed in to avoid capturing a transient inside a singleton.
    public async Task LoadAsync(AdminApiService adminApi)
    {
        if (IsLoaded) return;

        try
        {
            var flags = await adminApi.GetFeatureVisibilityAsync();
            if (flags is not null)
                _flags = flags;
        }
        catch
        {
            // Backend unavailable — keep all-true defaults so the app remains usable.
        }
        finally
        {
            IsLoaded = true;
        }
    }

    // Individual feature checks — all default to true.
    public bool RecommendedWorkflow  => _flags.RecommendedWorkflow;
    public bool UserGuide            => _flags.UserGuide;
    public bool Dashboard            => _flags.Dashboard;
    public bool SpecificationReview  => _flags.SpecificationReview;
    public bool QaArtifactLibrary    => _flags.QaArtifactLibrary;
    public bool CreateTestScenario   => _flags.CreateTestScenario;
    public bool TraceabilityCoverage => _flags.TraceabilityCoverage;
    public bool CodeTraceability     => _flags.CodeTraceability;
    public bool SpecComparison       => _flags.SpecComparison;
    public bool SpecificationDeltas  => _flags.SpecificationDeltas;
    public bool TaskDeltas           => _flags.TaskDeltas;
    public bool ImpactAnalysis       => _flags.ImpactAnalysis;
    public bool SpecDrift            => _flags.SpecDrift;
    public bool ImplementationReview => _flags.ImplementationReview;
    public bool AiChangeReview       => _flags.AiChangeReview;
    public bool QaReadiness          => _flags.QaReadiness;
    public bool AdminSystemSettings  => _flags.AdminSystemSettings;

    // Section-level helpers: show the section header only if at least one child item is visible.
    public bool ShowSectionGettingStarted => RecommendedWorkflow || UserGuide;
    public bool ShowSectionReview         => Dashboard || SpecificationReview;
    public bool ShowSectionLibrary        => QaArtifactLibrary || CreateTestScenario;
    public bool ShowSectionTraceability   => TraceabilityCoverage || CodeTraceability;
    public bool ShowSectionAnalysis       => SpecComparison || SpecificationDeltas || TaskDeltas
                                             || ImpactAnalysis || SpecDrift || ImplementationReview;
    public bool ShowSectionAiReview       => AiChangeReview || QaReadiness;
    public bool ShowSectionAdmin          => AdminSystemSettings;
}
