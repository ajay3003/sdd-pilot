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
            // Backend unavailable — keep standard workflow defaults so the app remains usable.
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
    public bool SpecificationExplorer => _flags.SpecificationExplorer;
    public bool QaArtifactLibrary    => _flags.QaArtifactLibrary;
    public bool SampleProjects       => _flags.SampleProjects;
    public bool LegacyTraceabilityNavigationEnabled => _flags.LegacyTraceabilityNavigationEnabled;
    public bool TraceabilityCoverage    => LegacyTraceabilityNavigationEnabled && _flags.TraceabilityCoverage;
    public bool TraceabilitySuggestions => LegacyTraceabilityNavigationEnabled && _flags.TraceabilitySuggestions;
    public bool CodeTraceability        => LegacyTraceabilityNavigationEnabled && _flags.CodeTraceability;
    public bool SpecComparison       => _flags.SpecComparison;
    public bool SpecificationDeltas  => _flags.SpecificationDeltas;
    public bool TaskDeltas           => _flags.TaskDeltas;
    public bool ImpactAnalysis       => _flags.ImpactAnalysis;
    public bool SpecDrift            => _flags.SpecDrift;
    public bool ImplementationReview => _flags.ImplementationReview;
    public bool ImplementationTraceability  => _flags.ImplementationTraceability;
    // Hides the menu entry only; source snapshots, architecture and database analyses are untouched.
    public bool SourceAnalysis              => _flags.SourceAnalysis;
    public bool AzureEnvironmentAnalysis    => _flags.AzureEnvironmentAnalysis;
    public bool FrontendQualityReview      => _flags.FrontendQualityReview;
    public bool ApiQualityReview           => _flags.ApiQualityReview;
    public bool IntegrationQualityReview   => _flags.IntegrationQualityReview;
    public bool CriticalE2ERegression      => _flags.CriticalE2ERegression;
    public bool BlazorWasmSecurityReview    => _flags.BlazorWasmSecurityReview;
    public bool BlazorWasmPerformanceReview => _flags.BlazorWasmPerformanceReview;
    public bool TaskExplorer         => _flags.TaskExplorer;
    public bool ConstitutionExplorer => _flags.ConstitutionExplorer;
    public bool DataModelExplorer    => _flags.DataModelExplorer;
    public bool PlanExplorer           => _flags.PlanExplorer;
    public bool ArtifactTraceability     => _flags.ArtifactTraceability;
    public bool AiChangeReview           => _flags.AiChangeReview;
    public bool EnableExtractionReview => _flags.EnableExtractionReview;
    public bool EnableArchitectureView => _flags.EnableArchitectureView;
    public bool AdminSystemSettings  => _flags.AdminSystemSettings;
    public bool QualityReview        => _flags.QualityReview;

    public void ApplyLocalFlags(FeatureVisibilityDto flags)
    {
        _flags = flags;
        IsLoaded = true;
    }
}
