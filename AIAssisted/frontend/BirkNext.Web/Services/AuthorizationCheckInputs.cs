using BirkNext.AiCodeReview;
using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.RuntimeSecurity;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// What authorization-check suggestions are derived from for one Target Environment: its API targets (Endpoint Discovery owns them), the literal
/// endpoint authorization metadata of its latest Source Analysis snapshot, and the authorization expectations Test Packs declare. Read only; the
/// suggestions themselves are never stored until a person adds them.
/// </summary>
public sealed record AuthorizationCheckInputs(
    IReadOnlyList<ApiReviewTarget> Targets,
    IReadOnlyList<CodeEndpointAuthorization> SourceEndpoints,
    IReadOnlyList<TestPackAuthorizationExpectation> PackExpectations,
    string? SourceNote)
{
    public static readonly AuthorizationCheckInputs Empty = new([], [], [], null);
}

/// <summary>Loads <see cref="AuthorizationCheckInputs"/>. Every source is optional: a missing service or a failing call contributes nothing.</summary>
public sealed class AuthorizationCheckInputsLoader(IServiceProvider services)
{
    /// <summary>Source endpoints and pack expectations for an environment (targets are passed in by the caller, which already resolved them).</summary>
    public async Task<AuthorizationCheckInputs> LoadAsync(string environmentId, IReadOnlyList<ApiReviewTarget> targets, CancellationToken ct = default)
    {
        var (endpoints, note) = await SourceEndpointsAsync(environmentId, ct);
        var packs = await PackExpectationsAsync(ct);
        return new(targets, endpoints, packs, note);
    }

    /// <summary>The same inputs for the active Target Environment, resolving its API targets from Endpoint Discovery.</summary>
    public async Task<AuthorizationCheckInputs> LoadForActiveAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default)
    {
        if (services.GetService<IFrontendAnalysisContextFactory>() is not { } factory || services.GetService<IEndpointDiscoveryService>() is not { } discovery)
            return AuthorizationCheckInputs.Empty;
        var context = await factory.GetActiveContextAsync();
        if (context.ActiveTargetError is not null) return AuthorizationCheckInputs.Empty;
        await discovery.LoadAsync(js);
        var targets = ApiReviewTargetResolver.Resolve(context, discovery.GetSnapshot(context.ActiveProfile.Id));
        return await LoadAsync(context.ActiveProfile.Id, targets, ct);
    }

    private async Task<(IReadOnlyList<CodeEndpointAuthorization>, string?)> SourceEndpointsAsync(string environmentId, CancellationToken ct)
    {
        if (services.GetService<IIntegrationCatalogApiService>() is not { } api) return ([], null);
        try
        {
            var latest = (await api.ListSourceSnapshotsAsync(environmentId, ct))
                .Where(s => s.CodeRiskEvidence is not null).OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();
            if (latest?.CodeRiskEvidence is not { } evidence) return ([], "No Source Analysis snapshot with endpoint authorization metadata.");
            var withNames = evidence.Endpoints.Count(e => e.Policies is not null);
            return (evidence.Endpoints, withNames == 0 && evidence.Endpoints.Count > 0
                ? "The latest Source Analysis snapshot predates policy and role capture; re-analyze the source to derive role and policy checks."
                : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], "Source Analysis evidence could not be loaded.");
        }
    }

    private async Task<IReadOnlyList<TestPackAuthorizationExpectation>> PackExpectationsAsync(CancellationToken ct)
    {
        if (services.GetService<IIntegrationJourneysApiService>() is not { } api) return [];
        try { return (await api.TestPacksAsync(ct)).Value?.SelectMany(p => p.AuthorizationExpectations).ToList() ?? []; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return []; }
    }

    /// <summary>
    /// Client decision on whether active authorization checks may run here, mirroring (never replacing) the backend gate: Local/Dev/QA/Test only,
    /// protected execution available, and the Target Environment registered by the server.
    /// </summary>
    public static (bool? Permitted, string? Reason) ExecutionPermission(FrontendAnalysisContext? context, SecurityExecutionStatus? execution)
    {
        if (BirkNext.Web.Components.ApiAuthorizationScenariosPanel.ClientBlockedReason(context?.ActiveProfile.EnvironmentType.ToString()) is { } blocked) return (false, blocked);
        if (execution is null) return (null, "The backend has not confirmed whether active authorization checks may run for this Target Environment.");
        if (!execution.ProtectedExecutionAvailable) return (false, SecurityExecutionStatus.AuthenticationRequiredMessage);
        if (!execution.Trust.Allowed) return (false, execution.Trust.Reason);
        return (true, null);
    }
}
