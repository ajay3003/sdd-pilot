using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Applicability;
using BirkNext.Technology;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Technology &amp; Analysis Coverage backend read (GET only): source technology inventory, configured integrations, domain extensions.</summary>
public interface ITechnologyCoverageApiService
{
    Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default);
}

public sealed class TechnologyCoverageApiService(HttpClient http) : ITechnologyCoverageApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default)
    {
        // Failures propagate: the caller classifies them (an HTTP error is not "the backend did not answer").
        return await http.GetFromJsonAsync<ProjectTechnologyCoverage>($"api/technology-coverage?environmentId={Uri.EscapeDataString(environmentId)}", Json, ct);
    }
}

/// <summary>
/// Per-session applicability of every review for the active Target Environment: backend source/catalog facts plus the target URL and the
/// artifact roles of the current workspace (<see cref="ICurrentWorkspaceProjection"/>), evaluated by the shared <see cref="ApplicabilityEvaluator"/>. Navigation and the coverage page read it; it never
/// hides a review — it labels it.
/// </summary>
public sealed class ProjectApplicabilityState : IDisposable
{
    private readonly ITechnologyCoverageApiService api;
    private readonly IFrontendAnalysisContextFactory contexts;
    private readonly ICurrentWorkspaceProjection workspace;
    private readonly IFrontendAnalysisSettingsService settings;
    private Task? _loading;

    public ProjectApplicabilityState(ITechnologyCoverageApiService api, IFrontendAnalysisContextFactory contexts, ICurrentWorkspaceProjection workspace,
        IFrontendAnalysisSettingsService settings)
    {
        this.api = api;
        this.contexts = contexts;
        this.workspace = workspace;
        this.settings = settings;
        // A saved Target Environment can change the active target, its URL and (environment-scoped) its source snapshot.
        this.settings.Changed += OnTargetEnvironmentsChanged;
    }

    private void OnTargetEnvironmentsChanged() => _ = RefreshAsync();

    public void Dispose() => settings.Changed -= OnTargetEnvironmentsChanged;
    public ProjectTechnologyCoverage? Coverage { get; private set; }
    public FrontendAnalysisProfile? Profile { get; private set; }
    public IReadOnlyDictionary<string, ReviewApplicability> Reviews { get; private set; } = new Dictionary<string, ReviewApplicability>();
    public bool Loaded { get; private set; }
    /// <summary>Why the coverage read failed, classified; null when it succeeded or nothing was requested (no target).</summary>
    public BackendRequestError? LoadError { get; private set; }
    public event Action? Changed;

    private int _generation;

    /// <summary>Loads once. Loaded is set before <see cref="Changed"/> fires, so a listener that calls this again while the first
    /// refresh is publishing (a synchronously completing refresh) returns at once instead of starting another refresh.</summary>
    public Task EnsureLoadedAsync() => Loaded ? Task.CompletedTask : _loading ??= RefreshAsync();

    /// <summary>Re-evaluates for the current profile, catalog and workspace (project switch, template applied, reset). When refreshes
    /// overlap, only the latest one is published, so an older response can never overwrite a newer state.</summary>
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        FrontendAnalysisProfile? profile = null;
        ProjectTechnologyCoverage? coverage = null;
        BackendRequestError? loadError = null;
        // Artifact roles come from the current workspace (Sample Project documents and imported artifacts alike), not session copies.
        var current = await workspace.GetAsync();
        try
        {
            // Without an active Target Environment (e.g. right after a local data reset) the context carries an empty placeholder profile:
            // that is "no target", not a backend that did not answer. Source technology needs no target: the current source snapshot is
            // read either way; only configured integrations and runtime targets come from the target.
            var context = await contexts.GetActiveContextAsync();
            profile = context.ActiveTargetError is null && context.ActiveProfile is { Id.Length: > 0 } active ? active : null;
            coverage = await api.GetAsync(profile?.Id ?? "");
        }
        catch (Exception ex) when (ex is InvalidOperationException || BackendRequestClassifier.IsRequestFailure(ex))
        {
            coverage = null;
            loadError = BackendRequestClassifier.FromException("Technology coverage", ex, "GET api/technology-coverage");
        }
        if (generation != _generation) return;
        Profile = profile;
        Coverage = coverage;
        LoadError = loadError;
        Reviews = ApplicabilityEvaluator.EvaluateAll(TechnologyCoveragePresentation.Input(Coverage, Profile,
            current.Has(WorkspaceArtifactType.Specification), current.Has(WorkspaceArtifactType.Plan) || current.Has(WorkspaceArtifactType.Constitution)));
        Loaded = true;
        Changed?.Invoke();
    }

    public ReviewApplicability? For(string reviewId) => Loaded && Reviews.TryGetValue(reviewId, out var a) ? a : null;
}

/// <summary>Pure presentation of support levels and applicability states. Neutral states use neutral tones — never the failure colour.</summary>
public static class TechnologyCoveragePresentation
{
    public static ProjectApplicabilityInput Input(ProjectTechnologyCoverage? coverage, FrontendAnalysisProfile? profile, bool hasRequirements, bool hasDocumentation)
    {
        var target = !string.IsNullOrWhiteSpace(profile?.TargetUrl);
        return new ProjectApplicabilityInput
        {
            HasSourceSnapshot = coverage?.SourceSnapshotId is not null,
            Technologies = coverage?.Source?.Technologies ?? [],
            Capabilities = coverage?.Source?.Capabilities ?? [],
            HasBrowserTarget = target, HasApiTarget = target,
            ConfiguredIntegrations = coverage?.ConfiguredIntegrations ?? [],
            HasRequirements = hasRequirements, HasDocumentation = hasDocumentation,
            HasSbom = coverage?.Source?.Technologies.Any(t => t.TechnologyId == "dependency.sbom") ?? false,
            DomainExtensions = coverage?.DomainExtensions ?? [],
            CiCdEvidenceOutdated = coverage?.CiCdEvidenceOutdated ?? false, CiCdEvidenceVersion = coverage?.CiCdEvidenceVersion,
        };
    }

    public static string Tone(SupportLevel level) => level switch
    {
        SupportLevel.Full => "complete",
        SupportLevel.Partial => "partial",
        _ => "muted",
    };

    /// <summary>Unsupported and NotApplicable are muted (neutral), never the attention/failure tone.</summary>
    public static string Tone(ApplicabilityStatus status) => status switch
    {
        ApplicabilityStatus.Applicable => "complete",
        ApplicabilityStatus.PartiallyApplicable or ApplicabilityStatus.NeedsConfiguration or ApplicabilityStatus.NeedsRefresh => "partial",
        _ => "muted",
    };

    /// <summary>Short navigation badge; null for Applicable (no badge needed).</summary>
    public static string? NavBadge(ReviewApplicability? a) => a?.Status switch
    {
        null or ApplicabilityStatus.Applicable => null,
        ApplicabilityStatus.PartiallyApplicable => "Partial",
        ApplicabilityStatus.NotApplicable => "N/A",
        ApplicabilityStatus.Unsupported => "Unsupported",
        ApplicabilityStatus.NeedsConfiguration => "Setup",
        ApplicabilityStatus.NeedsRefresh => "Needs refresh",
        _ => "No evidence",
    };

    public static string Confidence(BirkNext.Applicability.DetectionConfidence c) => c switch
    {
        BirkNext.Applicability.DetectionConfidence.Confirmed => "Confirmed",
        BirkNext.Applicability.DetectionConfidence.StronglySupported => "Strong",
        BirkNext.Applicability.DetectionConfidence.Inferred => "Inferred",
        _ => "Unresolved",
    };

    public static string Area(TechnologyArea area) => area switch
    {
        TechnologyArea.Language => "Languages",
        TechnologyArea.Framework => "Frameworks",
        TechnologyArea.Integration => "Integrations",
        TechnologyArea.Database => "Databases",
        TechnologyArea.Dependency => "Package ecosystems",
        TechnologyArea.Pipeline => "Pipelines",
        TechnologyArea.Cloud => "Cloud and platforms",
        TechnologyArea.Testing => "Test frameworks and results",
        _ => "Contracts",
    };
}
