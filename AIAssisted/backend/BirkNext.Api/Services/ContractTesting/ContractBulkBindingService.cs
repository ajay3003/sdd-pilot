using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ApiReview;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Configure one Target Environment for several contracts of one project at once: per contract the endpoint evidence (a saved binding is
/// authoritative; the contract's declared servers are candidates — path only, never a host), the proposed base path and authentication, whether
/// that authentication fits the operations' declared security (the same rule as test readiness), and the environment's credential reference.
/// Apply writes the existing per-contract binding (<see cref="ContractTestService.SaveBindingAsync"/>) only for rows the tester approved, with a
/// concurrency check and an explicit overwrite confirmation, and records each change. Nothing is sent to any API; no secret is copied.
/// </summary>
public sealed class ContractBulkBindingService(AppDbContext db, ContractRegistryService registry, ContractTestService tests, IApiEnvironmentSafetyPolicy safety,
    ApiServiceCredentialStore? credentialStore = null, ExecutionCredentialResolver? credentials = null, TimeProvider? clock = null)
{
    public const int MaxContracts = 200;
    public const string AudienceLimitation = "Token audience and scopes are not validated by BirkNext's credential resolver: a credential that fits the scheme may still be refused by the API.";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The environment gate shared with runs: an absolute http(s) origin without credentials, allowed by the safety policy (never production).</summary>
    private (bool Allowed, string? Problem, string Type, string Origin) Environment(ContractTestEnvironment e)
    {
        if (string.IsNullOrWhiteSpace(e.EnvironmentId)) return (false, "Select a Target Environment.", e.EnvironmentType, "");
        if (!Uri.TryCreate(e.TargetUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(u.UserInfo))
            return (false, "The Target Environment URL is not an absolute http(s) URL without credentials.", e.EnvironmentType, "");
        var decision = safety.Evaluate(new ApiReviewRunRequest
        {
            Environment = new() { EnvironmentId = e.EnvironmentId, EnvironmentType = e.EnvironmentType, TargetUrl = e.TargetUrl, IsProduction = e.IsProduction },
            Identity = new(BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManagedEdgeCdp, e.ProfileId, e.ContextFingerprint),
        });
        return (decision.ActiveTestingAllowed, decision.ActiveTestingAllowed ? null : decision.Reason, decision.EnvironmentType, u.GetLeftPart(UriPartial.Authority));
    }

    public async Task<BulkBindingPreview> PreviewAsync(BulkBindingRequest request, CancellationToken ct)
    {
        var (allowed, problem, type, origin) = Environment(request.Environment);
        var rows = new List<BulkBindingPreviewRow>();
        var group = string.IsNullOrWhiteSpace(request.ProjectKey) ? null : await registry.ProjectGroupAsync(request.ProjectKey, ct);
        foreach (var key in request.ContractKeys.Distinct(StringComparer.Ordinal).Take(MaxContracts))
            rows.Add(await RowAsync(key, request, group, allowed, problem, ct));
        return new(request.Environment.EnvironmentId, type, origin, allowed, problem, rows,
            [AudienceLimitation, "Endpoint evidence used: saved bindings and the contracts' declared OpenAPI servers (path part). Application configuration, Terraform and topology are not used as endpoint sources yet."]);
    }

    private async Task<BulkBindingPreviewRow> RowAsync(string key, BulkBindingRequest r, HashSet<string>? group, bool envAllowed, string? envProblem, CancellationToken ct)
    {
        var contract = await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct);
        if (contract is null)
            return new(key, key, ApiContractProtocol.Rest, null, null, [], EndpointEvidenceState.Unresolved, null, null, r.Authentication, [], "", BulkBindingStatus.Blocked, ["Contract not found."]);
        var protocol = Enum.Parse<ApiContractProtocol>(contract.Protocol);
        var latest = await registry.LatestVersionAsync(key, ct) ?? 0;
        var doc = await registry.DocumentAsync(key, latest, ct);
        var module = ContractRegistryService.ModuleOfSourcePath(await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderByDescending(v => v.Version).Select(v => v.SourcePath).FirstOrDefaultAsync(ct));
        var existing = await tests.BindingAsync(key, r.Environment.EnvironmentId, ct);
        var reasons = new List<string>();
        var blocked = false; var review = false;
        if (!envAllowed) { blocked = true; reasons.Add(envProblem ?? "The Target Environment is not allowed."); }
        if (group is null) { blocked = true; reasons.Add("Select the project these contracts belong to."); }
        else if (contract.ProjectKey is null || !group.Contains(contract.ProjectKey))
        { blocked = true; reasons.Add(contract.ProjectKey is null ? "This contract belongs to no Source Analysis project (uploaded, or its identity needs review)." : $"This contract belongs to project {contract.ProjectKey}, not to the selected project."); }
        if (contract.Lifecycle == nameof(ContractLifecycle.Archived)) { blocked = true; reasons.Add("Archived: read-only."); }

        // Endpoint evidence: a saved binding is authoritative; declared servers are candidates (path only).
        var candidates = new List<EndpointCandidate>();
        if (existing?.BasePath is { } saved) candidates.Add(new(saved, "Saved binding", $"saved {existing.UpdatedAt:yyyy-MM-dd HH:mm}"));
        var servers = doc is null ? [] : ContractTestService.ServerProposals(doc);
        candidates.AddRange(servers.Select(s => new EndpointCandidate(s.BasePath, "Declared server", s.Server)));
        var endpoint = existing?.BasePath is not null ? EndpointEvidenceState.Authoritative
            : servers.Count == 1 ? EndpointEvidenceState.Candidate : servers.Count > 1 ? EndpointEvidenceState.Ambiguous : EndpointEvidenceState.Unresolved;
        string? basePath = null;
        if (protocol == ApiContractProtocol.Rest)
        {
            switch (r.BasePathMode)
            {
                case BulkBasePathMode.Explicit:
                    if (ContractTestService.PathProblem(r.BasePath, "The base path") is { } pathProblem) { blocked = true; reasons.Add(pathProblem); }
                    basePath = Normalize(r.BasePath);
                    break;
                case BulkBasePathMode.FromContractServers:
                    if (servers.Count == 1) basePath = Normalize(servers[0].BasePath);
                    else if (servers.Count > 1) { review = true; reasons.Add($"The contract declares {servers.Count} server paths ({string.Join(", ", servers.Select(s => s.BasePath))}): choose one."); basePath = existing?.BasePath; }
                    else { review = true; reasons.Add("The contract declares no server path: the environment root would be used. Confirm or enter a path."); basePath = existing?.BasePath; }
                    break;
                default:
                    basePath = existing?.BasePath ?? (servers.Count == 1 ? Normalize(servers[0].BasePath) : null);
                    if (existing?.BasePath is null && servers.Count != 1)
                    { review = true; reasons.Add(servers.Count > 1 ? $"No saved path and {servers.Count} declared server paths: choose one." : "No saved path and no declared server path: the environment root would be used. Confirm or enter a path."); }
                    break;
            }
        }
        var graphQl = protocol == ApiContractProtocol.GraphQl ? Normalize(r.GraphQlPath) ?? existing?.GraphQlPath : existing?.GraphQlPath;
        if (protocol == ApiContractProtocol.GraphQl && ContractTestService.PathProblem(r.GraphQlPath, "The GraphQL endpoint path") is { } gqlProblem) { blocked = true; reasons.Add(gqlProblem); }

        // Authentication: the operations' declared security against the chosen kind (the readiness rule), then the environment's credential reference.
        var ops = doc?.Operations ?? [];
        var schemes = ops.SelectMany(ContractTestService.SecuritySchemes).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var misfits = ops.Select(o => ContractTestService.AuthenticationFitProblem(o, r.Authentication)).Where(p => p is not null).Distinct().ToList();
        if (misfits.Count > 0) { blocked = true; reasons.AddRange(misfits.Take(2)!); }
        var credential = CredentialState(r, ref blocked, reasons);
        var overwrites = existing is not null && (existing.BasePath != basePath || existing.GraphQlPath != graphQl || existing.Authentication != r.Authentication);
        if (overwrites) { review = true; reasons.Add($"Changes the saved binding ({Describe(existing!.BasePath, existing.GraphQlPath, existing.Authentication)}): confirm the overwrite."); }
        var status = blocked ? BulkBindingStatus.Blocked
            : existing is not null && !overwrites ? BulkBindingStatus.Unchanged
            : review ? BulkBindingStatus.NeedsReview : BulkBindingStatus.Applicable;
        return new(key, contract.Name, protocol, module, existing, candidates, endpoint, basePath, graphQl, r.Authentication, schemes, credential, status, reasons) { Overwrites = overwrites };
    }

    private string CredentialState(BulkBindingRequest r, ref bool blocked, List<string> reasons)
    {
        switch (r.Authentication)
        {
            case ExecutionAuthenticationKind.None:
                return "None (anonymous)";
            case ExecutionAuthenticationKind.ClientCredentials or ExecutionAuthenticationKind.ApiKey:
                var view = credentialStore?.Get(r.Environment.EnvironmentId);
                if (view is null || view.Kind != r.Authentication || !view.SecretStored)
                {
                    blocked = true;
                    reasons.Add($"No {ExecutionCredentialResolver.Label(r.Authentication)} credential is configured for this environment: configure it once (Contract Testing → endpoint panel); it is never copied per contract.");
                    return "missing";
                }
                return $"{view.Reference} (v{view.Version}{(view.Scope is { Length: > 0 } scope ? $", scope {scope}" : "")})";
            default: // attended session: the tester's own sign-in through the Local HTTPS proxy, checked when tests are listed and run
                if (credentials is not null && Uri.TryCreate(r.Environment.TargetUrl, UriKind.Absolute, out var target))
                {
                    var prepared = credentials.Prepare(new ExecutionRunRequest
                    {
                        EnvironmentId = r.Environment.EnvironmentId, EnvironmentType = r.Environment.EnvironmentType, TargetUrl = r.Environment.TargetUrl, ProfileId = r.Environment.ProfileId,
                        ContextFingerprint = r.Environment.ContextFingerprint, Authentication = r.Authentication,
                    }, new Uri(target.GetLeftPart(UriPartial.Authority)));
                    if (prepared.Problem is { } p) { reasons.Add($"Attended session: {p} Tests stay not ready until you sign in; the binding can still be saved."); return "attended session (not active now)"; }
                    return $"attended session ({prepared.Reference})";
                }
                return "attended session (checked at run time)";
        }
    }

    /// <summary>
    /// Applies the approved rows. Each row is re-evaluated: still Applicable (or NeedsReview with the explicit overwrite confirmation), unchanged
    /// since the preview (concurrency), then saved through the one binding writer and recorded. Per-row results; a partial apply is reported as such.
    /// </summary>
    public async Task<BulkBindingApplyResult> ApplyAsync(BulkBindingApplyRequest request, string appliedBy, CancellationToken ct)
    {
        var rows = new List<BulkBindingApplyRow>();
        if (!request.ConfirmEnvironment)
            return new([.. request.Items.Select(i => new BulkBindingApplyRow(i.Key, BulkBindingStatus.Skipped, "Not applied: confirm that this Target Environment is the approved one.", null))]);
        var group = string.IsNullOrWhiteSpace(request.ProjectKey) ? null : await registry.ProjectGroupAsync(request.ProjectKey, ct);
        var (allowed, problem, _, _) = Environment(request.Environment);
        foreach (var item in request.Items.DistinctBy(i => i.Key).Take(MaxContracts))
        {
            try
            {
                var mode = item.BasePath is null ? BulkBasePathMode.KeepExisting : BulkBasePathMode.Explicit;
                var check = await RowAsync(item.Key, new BulkBindingRequest
                {
                    ProjectKey = request.ProjectKey, Environment = request.Environment, ContractKeys = [item.Key], Authentication = item.Authentication, BasePathMode = mode,
                    BasePath = item.BasePath, GraphQlPath = item.GraphQlPath,
                }, group, allowed, problem, ct);
                if (check.Existing is { } now && item.ExpectedUpdatedAt is { } seen && now.UpdatedAt != seen)
                { rows.Add(new(item.Key, BulkBindingStatus.NeedsReview, $"The binding changed since the preview (now saved {now.UpdatedAt:yyyy-MM-dd HH:mm:ss}): preview again.", now)); continue; }
                if (check.Existing is not null && item.ExpectedUpdatedAt is null)
                { rows.Add(new(item.Key, BulkBindingStatus.NeedsReview, "A binding was saved since the preview: preview again.", check.Existing)); continue; }
                if (check.Status == BulkBindingStatus.Blocked) { rows.Add(new(item.Key, BulkBindingStatus.Blocked, string.Join(" ", check.Reasons), check.Existing)); continue; }
                if (check.Status == BulkBindingStatus.Unchanged) { rows.Add(new(item.Key, BulkBindingStatus.Unchanged, "Already bound like this.", check.Existing)); continue; }
                if (check.Status == BulkBindingStatus.NeedsReview && !item.ConfirmOverwrite)
                { rows.Add(new(item.Key, BulkBindingStatus.NeedsReview, "Not applied: it needs your review (" + string.Join(" ", check.Reasons) + ") — confirm it explicitly.", check.Existing)); continue; }
                var (saved, error) = await tests.SaveBindingAsync(item.Key, new ContractEndpointBindingSave(request.Environment.EnvironmentId, check.ProposedBasePath, check.ProposedGraphQlPath, item.Authentication), ct);
                if (saved is null) { rows.Add(new(item.Key, BulkBindingStatus.Failed, error ?? "Not saved.", check.Existing)); continue; }
                var contractId = await db.ApiContracts.AsNoTracking().Where(c => c.Key == item.Key).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
                db.ContractReviewDecisions.Add(new Models.ContractReviewDecisionRow
                {
                    Id = Guid.NewGuid(), Kind = "binding", ContractId = contractId, ProjectKey = request.ProjectKey, SubjectKey = request.Environment.EnvironmentId,
                    Detail = JsonSerializer.Serialize(new { before = check.Existing is null ? (object)"(none)" : check.Existing, after = saved, origin = request.Environment.TargetUrl }, ExecutionEvidenceStore.Json),
                    DecidedBy = string.IsNullOrWhiteSpace(appliedBy) ? "local user" : appliedBy, DecidedAt = _clock.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
                rows.Add(new(item.Key, BulkBindingStatus.Applied, check.Reasons.Count > 0 ? "Applied. " + string.Join(" ", check.Reasons) : "Applied.", saved));
            }
            catch (DbUpdateException)
            {
                rows.Add(new(item.Key, BulkBindingStatus.Failed, "Not saved (a concurrent change); preview again.", null));
                db.ChangeTracker.Clear();
            }
        }
        return new(rows);
    }

    private static string? Normalize(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim().TrimEnd('/') is { Length: > 0 } p ? p : null;

    private static string Describe(string? basePath, string? graphQl, ExecutionAuthenticationKind auth) =>
        $"{(basePath ?? graphQl ?? "environment root")}, {ExecutionCredentialResolver.Label(auth)}";
}
