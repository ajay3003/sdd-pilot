using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Dependencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// Explicit dependency-health policy. Nothing here has an invented default except the inventory freshness window, which is shown with its
/// configuration key wherever it is applied. No license list, maximum age, version lag or stale-PR age applies unless configured.
/// </summary>
public sealed class DependencyHealthOptions
{
    public const string SectionName = "DependencyReview";
    public int InventoryStaleAfterDays { get; set; } = 30;
    public int? StalePullRequestAfterDays { get; set; }
    public List<string> AllowedLicenses { get; set; } = [];
    public List<string> DeniedLicenses { get; set; } = [];
    /// <summary>
    /// Package-name globs (e.g. "M2LB.*") for internal packages. Matching names are never sent to public registries or advisory sources;
    /// their registry/advisory evidence is "private registry metadata unavailable", never "missing". Empty = every name is looked up.
    /// </summary>
    public List<string> PrivatePackagePatterns { get; set; } = [];
    public int RegistryConcurrency { get; set; } = 4;
    public int LookupTimeoutSeconds { get; set; } = 20;
}

public interface IDependencyHealthService
{
    Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default);
    Task<InventoryImportResult> ImportAsync(string fileName, byte[] bytes, SbomRole role, string? environment, string? name, CancellationToken ct = default);
    Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default);
    Task<(DependencyHealthRun? Run, string? Error)> RunAsync(DependencyHealthRequest request, CancellationToken ct = default);
    Task<(DependencyHealthRun? Run, string? Error)> RefreshAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<DependencyHealthRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<DependencyHealthRun?> GetAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>
/// Source-free dependency health review over a stored inventory: registry metadata, advisories, licenses, supply-chain identifiers,
/// comparisons and automation runtime — each with its source and retrieval time. Runs are immutable snapshots: opening one never re-queries a
/// registry; "refresh" creates a new run and leaves the old one unchanged. Network lookups are read-only, bounded, deduplicated and cached
/// (cached values keep their original retrieval time). No credential is logged, stored or exported.
/// </summary>
public sealed class DependencyHealthService(
    AppDbContext db, IEnumerable<IPackageRegistryProvider> registries, IAdvisoryProvider advisories, IDependencyAutomationSource automation,
    IDeployedDependencySource deployedSource, DependencyEvidenceCache cache, IOptions<DependencyHealthOptions> options, ILogger<DependencyHealthService> logger) : IDependencyHealthService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DependencyHealthOptions Options => options.Value;

    private bool IsPrivate(InventoryDependency dep) => Options.PrivatePackagePatterns.Any(p => RenovatePolicyEvaluator.Glob(p).IsMatch(dep.PackageName));

    private static RegistryClassifier.Classification PrivateClassification(string registry) => new(
        new RegistryObservation { Registry = registry, State = RegistryState.NotAssessed, Detail = $"Private package (DependencyReview:PrivatePackagePatterns): not sent to {registry}; private registry metadata unavailable." },
        VersionStatus.NotAssessed, "Not assessed: private package, private registry metadata unavailable.", null);

    // ── Inventories ─────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<List<DependencyInventorySnapshot>> AllInventoriesAsync(CancellationToken ct)
    {
        var stored = (await db.DependencyInventories.AsNoTracking().OrderByDescending(r => r.RecordedAt).Take(200).Select(r => r.SnapshotJson).ToListAsync(ct))
            .Select(j => JsonSerializer.Deserialize<DependencyInventorySnapshot>(j, Json)).OfType<DependencyInventorySnapshot>();
        var runs = (await db.DependencyReviewRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Take(50).Select(r => r.ResultJson).ToListAsync(ct))
            .Select(j => JsonSerializer.Deserialize<DependencyReviewResult>(j, Json)).OfType<DependencyReviewResult>();
        return [.. stored, .. runs.SelectMany(InventorySources.FromSourceReview)];
    }

    public async Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default)
    {
        var all = await AllInventoriesAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return all.Select(i => InventorySources.Summarize(i, all, Options.InventoryStaleAfterDays, now)).OrderByDescending(s => s.CapturedAt ?? s.RecordedAt).ToList();
    }

    private async Task StoreAsync(DependencyInventorySnapshot inventory, CancellationToken ct)
    {
        db.DependencyInventories.Add(new DependencyInventoryRecord { Id = inventory.Id, RecordedAt = inventory.RecordedAt, Name = inventory.Name[..Math.Min(300, inventory.Name.Length)],
            SourceType = inventory.SourceType.ToString(), SnapshotJson = JsonSerializer.Serialize(inventory, Json) });
        await db.SaveChangesAsync(ct);
    }

    public async Task<InventoryImportResult> ImportAsync(string fileName, byte[] bytes, SbomRole role, string? environment, string? name, CancellationToken ct = default)
    {
        var (validation, inventory) = InventorySources.Import(new InventorySources.ImportInput(System.IO.Path.GetFileName(fileName), bytes, role, environment, name, DateTimeOffset.UtcNow));
        if (inventory is null)
        {
            logger.LogInformation("Dependency inventory import rejected: {Format}, {Errors} validation error(s).", validation.Format, validation.Errors.Count);
            return new InventoryImportResult(validation, null, $"The document is not a valid {DependencyHealthLabels.Format(validation.Format)}; no inventory was created.");
        }
        await StoreAsync(inventory, ct);
        logger.LogInformation("Dependency inventory imported: {Format} {Components} component(s), stage {Stage}.", validation.Format, validation.Components, inventory.Stage);
        var all = await AllInventoriesAsync(ct);
        return new InventoryImportResult(validation, InventorySources.Summarize(inventory, all, Options.InventoryStaleAfterDays, DateTimeOffset.UtcNow), null);
    }

    public async Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.LookupTimeoutSeconds));
        var (inventory, error) = await deployedSource.CaptureAsync(request.TargetUrl, request.Environment, timeout.Token);
        if (inventory is null) return new InventoryImportResult(null, null, DependencyEvidenceRedaction.Redact(error));
        await StoreAsync(inventory, ct);
        logger.LogInformation("Deployed dependency evidence captured: {Items} assembl(ies).", inventory.Dependencies.Count);
        var all = await AllInventoriesAsync(ct);
        return new InventoryImportResult(null, InventorySources.Summarize(inventory, all, Options.InventoryStaleAfterDays, DateTimeOffset.UtcNow), null);
    }

    // ── Health runs ─────────────────────────────────────────────────────────────────────────────────────────────────

    public Task<(DependencyHealthRun? Run, string? Error)> RunAsync(DependencyHealthRequest request, CancellationToken ct = default) => RunCoreAsync(request, null, ct);

    public async Task<(DependencyHealthRun? Run, string? Error)> RefreshAsync(Guid runId, CancellationToken ct = default) =>
        await GetAsync(runId, ct) is { } previous ? await RunCoreAsync(previous.Request, runId, ct) : (null, "Dependency health run not found.");

    private async Task<(DependencyHealthRun?, string?)> RunCoreAsync(DependencyHealthRequest request, Guid? refreshOf, CancellationToken ct)
    {
        var all = await AllInventoriesAsync(ct);
        DependencyInventorySnapshot? Find(Guid? id) => id is null ? null : all.FirstOrDefault(i => i.Id == id);
        if (Find(request.InventoryId) is not { } inventory) return (null, "Choose a stored dependency inventory.");
        var baseline = Find(request.BaselineInventoryId);
        var comparison = Find(request.ComparisonInventoryId);
        var deployed = Find(request.DeployedInventoryId);
        if (request.BaselineInventoryId is not null && baseline is null || request.ComparisonInventoryId is not null && comparison is null || request.DeployedInventoryId is not null && deployed is null)
            return (null, "A selected comparison inventory is no longer stored.");
        var now = DateTimeOffset.UtcNow;
        var bypassCache = refreshOf is not null;

        // Registry: one lookup per distinct package name, bounded concurrency, per-call timeout, cache unless refreshing.
        var registryResults = new Dictionary<string, RegistryPackageResult>(StringComparer.OrdinalIgnoreCase);
        var provider = registries.FirstOrDefault(r => r.Manager == "nuget");
        var privateCount = inventory.Dependencies.Count(IsPrivate);
        var names = inventory.Dependencies.Where(d => RegistryClassifier.Supported(d) && !IsPrivate(d)).Select(d => d.PackageName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (provider is not null)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, Options.RegistryConcurrency));
            await Task.WhenAll(names.Select(async name =>
            {
                var key = $"registry:{provider.Registry}:{name.ToLowerInvariant()}";
                if (!bypassCache && cache.TryGet<RegistryPackageResult>(key, now, out var cached)) { lock (registryResults) registryResults[name] = cached with { FromCache = true }; return; }
                await gate.WaitAsync(ct);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Options.LookupTimeoutSeconds));
                    RegistryPackageResult result;
                    try { result = await provider.GetPackageAsync(name, timeout.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = new(RegistryState.Timeout, $"{provider.Registry} did not answer within {Options.LookupTimeoutSeconds} s.", [], DateTimeOffset.UtcNow); }
                    result = result with { Detail = DependencyEvidenceRedaction.Redact(result.Detail) };
                    if (result.State is RegistryState.Observed or RegistryState.PackageNotFound) cache.Set(key, result, result.RetrievedAt);
                    lock (registryResults) registryResults[name] = result;
                }
                finally { gate.Release(); }
            }));
        }

        // Advisories: cached per package; the rest in one bounded batch lookup.
        var advisoryByPackage = new Dictionary<string, (List<AdvisoryRecord>? Records, string? Failure, DateTimeOffset At, bool Cached)>(StringComparer.OrdinalIgnoreCase);
        var advisoryNames = inventory.Dependencies.Where(d => advisories.Supports(d) && !IsPrivate(d)).Select(d => d.PackageName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        foreach (var name in advisoryNames)
            if (!bypassCache && cache.TryGet<(List<AdvisoryRecord>, DateTimeOffset)>($"advisory:{advisories.Name}:{name.ToLowerInvariant()}", now, out var hit)) advisoryByPackage[name] = (hit.Item1, null, hit.Item2, true);
            else missing.Add(name);
        if (missing.Count > 0)
        {
            AdvisoryLookup lookup;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Options.LookupTimeoutSeconds * 3));
                try { lookup = await advisories.LookupAsync(missing, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { lookup = new AdvisoryLookup([], missing.ToDictionary(n => n, _ => $"{advisories.Name} did not answer within the timeout.", StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow); }
            }
            foreach (var name in missing)
                if (lookup.Failed.TryGetValue(name, out var failure)) advisoryByPackage[name] = (null, DependencyEvidenceRedaction.Redact(failure), lookup.RetrievedAt, false);
                else
                {
                    var records = lookup.ByPackage.GetValueOrDefault(name) ?? [];
                    advisoryByPackage[name] = (records, null, lookup.RetrievedAt, false);
                    cache.Set($"advisory:{advisories.Name}:{name.ToLowerInvariant()}", (records, lookup.RetrievedAt), lookup.RetrievedAt);
                }
        }

        // Renovate policy for the security-fix cross-check: the chosen source review, or the one this inventory was derived from.
        var (evaluator, policyRepo, policySource, staticConfiguration) = await PolicyAsync(request, inventory, ct);

        var comparisons = new List<InventoryComparison>();
        InventoryComparison? crossSource = null;
        if (comparison is not null)
        {
            var (source, other) = comparison.Stage == InventoryStage.Declared && inventory.Stage != InventoryStage.Declared ? (comparison, inventory) : (inventory, comparison);
            comparisons.Add(crossSource = InventoryComparer.SourceVsSbom(source, other));
        }
        if (deployed is not null) comparisons.Add(InventoryComparer.ExpectedVsDeployed(inventory, deployed));
        if (baseline is not null) comparisons.Add(InventoryComparer.Baseline(baseline, inventory));
        var stageInventories = new[] { inventory, comparison, deployed }.OfType<DependencyInventorySnapshot>().ToList();

        var groups = inventory.Dependencies.GroupBy(d => $"{InventorySources.Key(d)}@{(d.Version ?? d.VersionRange ?? "?").ToLowerInvariant()}").ToList();
        var items = groups.Select(group =>
        {
            var dep = group.First();
            var isPrivate = IsPrivate(dep);
            var classification = isPrivate ? PrivateClassification(provider?.Registry ?? "public registries")
                : RegistryClassifier.Classify(dep, provider is not null && RegistryClassifier.Supported(dep) ? registryResults.GetValueOrDefault(dep.PackageName) : null, provider?.Registry ?? "", now);
            var security = isPrivate
                ? new SecurityEvidence { State = AdvisoryState.NotAssessed, Detail = "Private package (DependencyReview:PrivatePackagePatterns): not sent to the public advisory source. Not assessed is not \"no advisories\"." }
                : SecurityFor(dep, advisoryByPackage);
            var license = License(dep, classification.Registry, provider?.Registry);
            RemediationPolicyCheck? remediation = null;
            if (security.State == AdvisoryState.Affected)
                remediation = evaluator is null
                    ? new RemediationPolicyCheck { State = RemediationPolicyState.NotAssessed, FixedVersion = security.FixedIn, Explanation = "No source review with a Renovate configuration was selected." }
                    : Remediation(evaluator, policyRepo!, dep, security.FixedIn, policySource!);
            var key = InventorySources.Key(dep);
            return new DependencyHealthItem
            {
                Key = group.Key, Dependency = dep, Locations = group.Select(d => d.Location).OfType<string>().Distinct().ToList(), Registry = classification.Registry, VersionStatus = classification.Status, VersionDetail = classification.Detail,
                ObservedAgeDays = classification.AgeDays, Security = security, License = license, Remediation = remediation, Stages = InventoryComparer.Stages(dep, stageInventories),
                Conflicts = crossSource?.Entries.Where(e => e.State == ComparisonState.EvidenceConflict && $"{e.PackageManager}:{e.PackageName}".Equals($"{dep.PackageManager}:{dep.PackageName}", StringComparison.OrdinalIgnoreCase))
                    .Select(e => $"{crossSource.LeftName}: {e.LeftValue} · {crossSource.RightName}: {e.RightValue}").ToList() ?? [],
                History = baseline?.Dependencies.Where(b => InventorySources.Key(b) == key && (b.Version ?? b.VersionRange) != (dep.Version ?? dep.VersionRange))
                    .Select(b => $"{b.Version ?? b.VersionRange ?? "unknown"} ({baseline.Name})").Distinct().ToList() ?? [],
            };
        }).ToList();

        var automationEvidence = await AutomationAsync(inventory, inventory.Repository ?? policyRepo?.Repository, staticConfiguration, ct);
        var (freshness, freshnessDetail) = InventorySources.Freshness(inventory, all, Options.InventoryStaleAfterDays, now);
        var summary = Summarize(items) with { Declarations = inventory.Dependencies.Count };
        var run = new DependencyHealthRun
        {
            RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow, RefreshOf = refreshOf, Request = request, Inventory = inventory, Freshness = freshness, FreshnessDetail = freshnessDetail,
            Label = string.IsNullOrWhiteSpace(request.Label) ? inventory.Name : request.Label.Trim(), Items = items, Summary = summary, Comparisons = comparisons, Automation = automationEvidence,
            PolicySource = policySource,
            Sources = Sources(inventory, items, provider, advisoryNames.Count, policySource, automationEvidence, deployed, advisoryByPackage),
            Categories = Categories(inventory, freshness, freshnessDetail, summary, items, policySource, automationEvidence, comparisons),
            Observations = Observations(items, comparisons, freshness, freshnessDetail, provider?.Registry),
            Limitations =
            [
                .. inventory.Limitations,
                "Latest published stable is what the registry lists — not a recommended version. Outdated is not vulnerable.",
                "No matched advisory is not a safety claim; vulnerable is not proven exploitable.",
                "Runtime loading is not assessed by any source in this review.",
                .. Options.AllowedLicenses.Count + Options.DeniedLicenses.Count == 0 ? new[] { "No license policy is configured (DependencyReview:AllowedLicenses / DeniedLicenses): license metadata is evidence only." } : [],
                .. provider is null ? new[] { "No package registry provider is registered." } : [],
                .. privateCount > 0
                    ? new[] { $"{privateCount} item(s) matched DependencyReview:PrivatePackagePatterns ({string.Join(", ", Options.PrivatePackagePatterns)}) and were not sent to public registries or advisory sources: private registry metadata unavailable." }
                    : names.Count > 0 ? [$"Package names of this inventory were sent to {provider?.Registry} and {advisories.Name} as read-only lookups. Configure DependencyReview:PrivatePackagePatterns to keep internal package names local."] : [],
                .. inventory.Dependencies.Any(d => !RegistryClassifier.Supported(d)) ? new[] { $"Registry and advisory checks cover NuGet only; {inventory.Dependencies.Count(d => !RegistryClassifier.Supported(d))} other item(s) (Docker images, SDKs, …) are Not assessed." } : [],
            ],
        };
        db.DependencyHealthRuns.Add(new DependencyHealthRunRecord { Id = run.RunId, CompletedAt = run.CompletedAt, Label = run.Label[..Math.Min(300, run.Label.Length)], InventoryId = inventory.Id, ResultJson = JsonSerializer.Serialize(run, Json) });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Dependency health run {RunId}: {Dependencies} dependencies, registry {Observed}/{Unavailable} observed/unavailable, {Affected} affected, automation {Automation}, refresh of {RefreshOf}.",
            run.RunId, summary.Dependencies, summary.RegistryObserved, summary.RegistryUnavailable, summary.AffectedDependencies, automationEvidence.State, refreshOf);
        return (run, null);
    }

    private static SecurityEvidence SecurityFor(InventoryDependency dep, Dictionary<string, (List<AdvisoryRecord>? Records, string? Failure, DateTimeOffset At, bool Cached)> byPackage)
    {
        if (!byPackage.TryGetValue(dep.PackageName, out var entry)) return AdvisoryMatcher.Evaluate(dep, null, null);
        var lookup = new AdvisoryLookup(entry.Records is null ? [] : new(StringComparer.OrdinalIgnoreCase) { [dep.PackageName] = entry.Records },
            entry.Failure is null ? [] : new(StringComparer.OrdinalIgnoreCase) { [dep.PackageName] = entry.Failure }, entry.At);
        var evidence = AdvisoryMatcher.Evaluate(dep, OsvNameOnly.Instance, lookup);
        return entry.Cached ? evidence with { Detail = evidence.Detail + $" (cached, retrieved {entry.At.ToUniversalTime():yyyy-MM-dd HH:mm} UTC)" } : evidence;
    }

    /// <summary>The matcher only needs the provider's name and support rule; the lookup has already happened.</summary>
    private sealed class OsvNameOnly : IAdvisoryProvider
    {
        public static readonly OsvNameOnly Instance = new();
        public string Name => "OSV (osv.dev)";
        public bool Supports(InventoryDependency dep) => RegistryClassifier.Supported(dep);
        public Task<AdvisoryLookup> LookupAsync(IReadOnlyList<string> packageNames, CancellationToken ct) => throw new NotSupportedException();
    }

    private LicenseEvidence License(InventoryDependency dep, RegistryObservation registry, string? registryName)
    {
        var statements = new List<(string License, string Source)>();
        statements.AddRange(dep.Licenses.Select(l => (l, DependencyHealthLabels.Source(dep.Stage == InventoryStage.Resolved ? InventorySourceType.LockFile : InventorySourceType.Sbom))));
        if (registry.LicenseExpression is { } expression) statements.Add((expression, $"{registryName} registry"));
        var distinct = statements.Select(s => s.License).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var consulted = dep.LicenseFieldPresent || registry.State is RegistryState.Observed or RegistryState.Unlisted;
        var state = distinct.Count switch { 0 => consulted ? LicenseState.NotDeclared : LicenseState.Unknown, 1 => LicenseState.Detected, _ => LicenseState.Multiple };
        var (policy, policyDetail) = LicensePolicy(distinct);
        var detail = state switch
        {
            LicenseState.Detected => $"License metadata: {distinct[0]} ({string.Join(", ", statements.Select(s => s.Source).Distinct())}).",
            LicenseState.Multiple => $"Sources state different license metadata: {string.Join("; ", statements.Select(s => $"{s.License} ({s.Source})"))}.",
            LicenseState.NotDeclared => registry.LicenseUrl is { } url ? $"No license expression declared; only a license URL ({url})." : "The consulted sources declare no license.",
            _ => "No license metadata source was available for this dependency.",
        };
        return new LicenseEvidence { State = state, Licenses = distinct, Sources = statements.Select(s => s.Source).Distinct().ToList(), Policy = policy, Detail = $"{detail} {policyDetail}".Trim() };
    }

    /// <summary>License policy only when configured. An identifier on the deny list → Denied; every identifier allowed → Allowed.</summary>
    private (LicensePolicyState, string) LicensePolicy(List<string> licenses)
    {
        if (Options.AllowedLicenses.Count + Options.DeniedLicenses.Count == 0) return (LicensePolicyState.NotConfigured, "License policy: not configured — no approval or compliance claim.");
        if (licenses.Count == 0) return (LicensePolicyState.NotAssessed, "License policy: not assessed (no license metadata).");
        var ids = licenses.SelectMany(l => l.Split([' ', '(', ')'], StringSplitOptions.RemoveEmptyEntries)).Where(t => t is not ("OR" or "AND" or "WITH")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.FirstOrDefault(i => Options.DeniedLicenses.Contains(i, StringComparer.OrdinalIgnoreCase)) is { } denied) return (LicensePolicyState.Denied, $"License policy: {denied} is on the configured deny list.");
        return ids.All(i => Options.AllowedLicenses.Contains(i, StringComparer.OrdinalIgnoreCase))
            ? (LicensePolicyState.Allowed, "License policy: every identifier is on the configured allow list.")
            : (LicensePolicyState.NotInPolicy, $"License policy: {string.Join(", ", ids.Where(i => !Options.AllowedLicenses.Contains(i, StringComparer.OrdinalIgnoreCase)))} not on the configured allow list.");
    }

    private async Task<(RenovatePolicyEvaluator?, RepositoryDependencyReview?, string?, string?)> PolicyAsync(DependencyHealthRequest request, DependencyInventorySnapshot inventory, CancellationToken ct)
    {
        var runId = request.PolicyRunId ?? (inventory.SourceType == InventorySourceType.SourceReview ? inventory.SourceRunId : null);
        if (runId is null) return (null, null, null, null);
        var json = await db.DependencyReviewRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        if (json is null || JsonSerializer.Deserialize<DependencyReviewResult>(json, Json) is not { } run) return (null, null, null, null);
        var repository = request.PolicyRepository ?? (request.PolicyRunId is null ? inventory.Repository : null) ?? inventory.Repository;
        var repo = run.Repositories.FirstOrDefault(r => string.Equals(r.Repository, repository, StringComparison.OrdinalIgnoreCase)) ?? (run.Repositories.Count == 1 ? run.Repositories[0] : null);
        if (repo is null) return (null, null, null, null);
        var staticConfig = $"Renovate configuration (static, from source review {run.CompletedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC): {DependencyLabels.Category(repo.Coverage switch { RenovateCoverage.Configured or RenovateCoverage.Inherited => ReviewCategoryState.Ready, RenovateCoverage.Missing => ReviewCategoryState.Missing, _ => ReviewCategoryState.Partial })} — {repo.CoverageDetail}";
        if (repo.NormalizedConfig is null || JsonNode.Parse(repo.NormalizedConfig) is not JsonObject config) return (null, repo, null, staticConfig);
        return (new RenovatePolicyEvaluator(config, repo.UnresolvedPresets), repo, $"Renovate policy snapshot of {repo.Repository}, source review {run.RunId:D} ({run.CompletedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC)", staticConfig);
    }

    /// <summary>What the stored Renovate policy would do with the advisory's fixed version — evaluated, never applied.</summary>
    public static RemediationPolicyCheck Remediation(RenovatePolicyEvaluator evaluator, RepositoryDependencyReview repo, InventoryDependency dep, string? fixedIn, string policySource)
    {
        if (fixedIn is null) return new RemediationPolicyCheck { State = RemediationPolicyState.NotAssessable, PolicySource = policySource, Explanation = "The advisory states no fixed version to test against the policy." };
        var declared = repo.Dependencies.FirstOrDefault(d => d.PackageName.Equals(dep.PackageName, StringComparison.OrdinalIgnoreCase) && dep.Location?.StartsWith(d.OwnerFile, StringComparison.Ordinal) == true)
            ?? repo.Dependencies.FirstOrDefault(d => d.PackageName.Equals(dep.PackageName, StringComparison.OrdinalIgnoreCase) && d.Manager == dep.PackageManager);
        var subject = (declared ?? new DeclaredDependency { Repository = repo.Repository, Manager = dep.PackageManager, Datasource = dep.Datasource ?? dep.PackageManager, PackageName = dep.PackageName, OwnerFile = "" })
            with { CurrentValue = dep.Version, IsRange = false };
        var sim = evaluator.Simulate(subject, "Advisory fixed version", fixedIn, null);
        var state = sim.Result switch
        {
            PolicyResult.Allowed => RemediationPolicyState.PermittedByPolicy,
            PolicyResult.DeferredBySchedule => RemediationPolicyState.PermittedWithinSchedule,
            PolicyResult.RequiresApproval => RemediationPolicyState.RequiresApproval,
            PolicyResult.Blocked => RemediationPolicyState.BlockedByPolicy,
            PolicyResult.BlockedByVersionConstraint => RemediationPolicyState.BlockedByVersionConstraint,
            PolicyResult.Ignored => RemediationPolicyState.IgnoredByRenovate,
            PolicyResult.NotProposed => RemediationPolicyState.NotProposed,
            _ => RemediationPolicyState.NotAssessable,
        };
        return new RemediationPolicyCheck
        {
            State = state, FixedVersion = fixedIn, UpdateType = sim.UpdateType, MatchedRules = sim.MatchedRules.Select(m => m.RuleIndex).ToList(), PolicySource = policySource,
            Explanation = $"{DependencyHealthLabels.Remediation(state)}. Fixed version {fixedIn} ({sim.UpdateType.ToString().ToLowerInvariant()} from {dep.Version}) evaluated against the stored configuration: {sim.Explanation}"
                + (sim.Limitations.Count > 0 ? $" {string.Join(" ", sim.Limitations)}" : "") + " The configuration is not changed.",
        };
    }

    private async Task<AutomationEvidence> AutomationAsync(DependencyInventorySnapshot inventory, string? repository, string? staticConfiguration, CancellationToken ct)
    {
        AutomationEvidence evidence;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Options.LookupTimeoutSeconds * 2));
            try { evidence = await automation.GetAsync(repository, Options.StalePullRequestAfterDays, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { evidence = new AutomationEvidence { State = AutomationState.ProviderUnavailable, Repository = repository, Detail = "The repository provider did not answer within the timeout." }; }
        }
        return evidence with
        {
            Detail = DependencyEvidenceRedaction.Redact(evidence.Detail), StaticConfiguration = staticConfiguration,
            OpenPullRequests = evidence.OpenPullRequests.Select(p =>
            {
                // "Current" comes from the reviewed inventory, "proposed" from the PR; the update type is computed, never the PR's safety.
                var current = p.PackageName is null ? null : inventory.Dependencies.FirstOrDefault(d => d.PackageName.Equals(p.PackageName, StringComparison.OrdinalIgnoreCase) && d.Version is not null)?.Version;
                var type = current is not null && p.ToVersion is not null ? RenovatePolicyEvaluator.UpdateType(current, p.ToVersion, "nuget").Type : (DependencyUpdateType?)null;
                return p with { Title = DependencyEvidenceRedaction.Redact(p.Title), Url = p.Url is null ? null : DependencyEvidenceRedaction.Redact(p.Url), FromVersion = p.FromVersion ?? current, UpdateType = type };
            }).ToList(),
        };
    }

    // ── Summary, categories, observations ───────────────────────────────────────────────────────────────────────────

    public static DependencyHealthSummary Summarize(List<DependencyHealthItem> items) => new()
    {
        Dependencies = items.Count,
        RegistryObserved = items.Count(i => i.Registry.State is RegistryState.Observed or RegistryState.Unlisted),
        RegistryNotFound = items.Count(i => i.Registry.State is RegistryState.PackageNotFound or RegistryState.VersionNotFound),
        RegistryUnavailable = items.Count(i => i.Registry.State is RegistryState.Unauthorized or RegistryState.RateLimited or RegistryState.Timeout or RegistryState.RegistryUnavailable or RegistryState.ProviderError),
        RegistryNotAssessed = items.Count(i => i.Registry.State == RegistryState.NotAssessed),
        Current = items.Count(i => i.VersionStatus is VersionStatus.Current or VersionStatus.NewerThanLatestStable),
        PatchBehind = items.Count(i => i.VersionStatus == VersionStatus.PatchBehind), MinorBehind = items.Count(i => i.VersionStatus == VersionStatus.MinorBehind),
        MajorBehind = items.Count(i => i.VersionStatus == VersionStatus.MajorBehind), VersionNotComparable = items.Count(i => i.VersionStatus is VersionStatus.NotComparable or VersionStatus.VersionUnavailable),
        AffectedDependencies = items.Count(i => i.Security.State == AdvisoryState.Affected), AdvisorySourceUnavailable = items.Count(i => i.Security.State == AdvisoryState.AdvisorySourceUnavailable),
        NoMatchedAdvisory = items.Count(i => i.Security.State is AdvisoryState.NoMatchedAdvisoryObserved or AdvisoryState.NotAffectedByMatchedAdvisory),
        SecurityNotAssessed = items.Count(i => i.Security.State is AdvisoryState.NotAssessed or AdvisoryState.VersionNotComparable),
        DeprecatedOrUnlisted = items.Count(i => i.Registry.Deprecation is not null || i.Registry.State == RegistryState.Unlisted),
        LicenseDetected = items.Count(i => i.License.State is LicenseState.Detected or LicenseState.Multiple), LicenseUnknown = items.Count(i => i.License.State is LicenseState.Unknown or LicenseState.NotDeclared),
    };

    private List<EvidenceSourceStatus> Sources(DependencyInventorySnapshot inventory, List<DependencyHealthItem> items, IPackageRegistryProvider? provider, int advisoryCandidates, string? policySource,
        AutomationEvidence automationEvidence, DependencyInventorySnapshot? deployed, Dictionary<string, (List<AdvisoryRecord>? Records, string? Failure, DateTimeOffset At, bool Cached)> advisoryByPackage)
    {
        var registryItems = items.Where(i => i.Registry.State != RegistryState.NotAssessed || RegistryClassifier.Supported(i.Dependency)).ToList();
        var registryFailed = registryItems.Count(i => i.VersionStatus == VersionStatus.RegistryUnavailable);
        var cached = items.Count(i => i.Registry.FromCache);
        var advisoryFailed = advisoryByPackage.Values.Count(v => v.Failure is not null);
        return
        [
            new EvidenceSourceStatus { Category = CheckCategory.Inventory, Name = inventory.Name, State = "Used", RetrievedAt = inventory.CapturedAt, Detail = inventory.Provenance },
            new EvidenceSourceStatus { Category = CheckCategory.Registry, Name = provider?.Registry ?? "Package registry", RetrievedAt = items.Select(i => i.Registry.RetrievedAt).Where(t => t is not null).Min(),
                State = provider is null || registryItems.Count == 0 ? "Not assessed" : registryFailed == registryItems.Count ? "Unavailable" : registryFailed > 0 ? "Partial" : "Used",
                Detail = provider is null ? "No registry provider." : $"GET-only metadata lookups; {registryFailed} unavailable{(cached > 0 ? $", {cached} served from cache with their original retrieval time" : "")}." },
            new EvidenceSourceStatus { Category = CheckCategory.Security, Name = advisories.Name, RetrievedAt = advisoryByPackage.Values.Select(v => (DateTimeOffset?)v.At).Min(),
                State = advisoryCandidates == 0 ? "Not assessed" : advisoryFailed == advisoryCandidates ? "Unavailable" : advisoryFailed > 0 ? "Partial" : "Used",
                Detail = $"{advisoryCandidates} package(s) queried; {advisoryFailed} unavailable (reported as \"Security evidence unavailable\", never as 0)." },
            new EvidenceSourceStatus { Category = CheckCategory.License, Name = "License metadata", State = items.Any(i => i.License.Sources.Count > 0) ? "Used" : "Not assessed",
                Detail = $"From {string.Join(" and ", items.SelectMany(i => i.License.Sources).Distinct().DefaultIfEmpty("no source"))}; policy {(Options.AllowedLicenses.Count + Options.DeniedLicenses.Count == 0 ? "not configured" : "configured")}." },
            new EvidenceSourceStatus { Category = CheckCategory.SourceConfiguration, Name = "Renovate policy", State = policySource is null ? "Not assessed" : "Used", Detail = policySource ?? "Not assessed — no source/config selected." },
            new EvidenceSourceStatus { Category = CheckCategory.Automation, Name = automationEvidence.Provider is { Length: > 0 } p ? p : "Repository provider", State = DependencyHealthLabels.Automation(automationEvidence.State),
                RetrievedAt = automationEvidence.RetrievedAt, Detail = automationEvidence.Detail },
            new EvidenceSourceStatus { Category = CheckCategory.Deployment, Name = deployed?.Name ?? "Deployed evidence", State = deployed is null ? "Not assessed" : "Used", RetrievedAt = deployed?.CapturedAt,
                Detail = deployed?.Provenance ?? "No deployed inventory selected." },
        ];
    }

    private static List<ReviewCategory> Categories(DependencyInventorySnapshot inventory, InventoryFreshness freshness, string freshnessDetail, DependencyHealthSummary s, List<DependencyHealthItem> items,
        string? policySource, AutomationEvidence automationEvidence, List<InventoryComparison> comparisons)
    {
        var registryAssessed = s.RegistryObserved + s.RegistryNotFound + s.RegistryUnavailable;
        var securityAssessed = items.Count(i => i.Security.State != AdvisoryState.NotAssessed);
        var remediations = items.Where(i => i.Remediation is { State: not RemediationPolicyState.NotAssessed }).ToList();
        var blocked = remediations.Count(i => i.Remediation!.State is RemediationPolicyState.BlockedByPolicy or RemediationPolicyState.BlockedByVersionConstraint or RemediationPolicyState.IgnoredByRenovate);
        var denied = items.Count(i => i.License.Policy == LicensePolicyState.Denied);
        var categories = new List<ReviewCategory>
        {
            new("Inventory", freshness == InventoryFreshness.Current ? ReviewCategoryState.Ready : ReviewCategoryState.Partial,
                $"{DependencyHealthLabels.Source(inventory.SourceType)} · {DependencyHealthLabels.Stage(inventory.Stage)} · {s.Dependencies} dependencies · {freshness}: {freshnessDetail}"),
            new("Version health", registryAssessed == 0 ? ReviewCategoryState.NotAssessed : s.RegistryUnavailable > 0 ? ReviewCategoryState.Partial : ReviewCategoryState.Ready,
                registryAssessed == 0 ? "No registry evidence for this inventory." : $"{s.PatchBehind} patch behind · {s.MinorBehind} minor behind · {s.MajorBehind} major behind · {s.Current} current · {s.VersionNotComparable} not comparable. Registry: {s.RegistryObserved} observed, {s.RegistryNotFound} not found, {s.RegistryUnavailable} unavailable. Being behind is evidence, not a defect."),
            new("Security advisories", securityAssessed == 0 ? ReviewCategoryState.NotAssessed : s.AffectedDependencies > 0 ? ReviewCategoryState.NeedsReview : s.AdvisorySourceUnavailable > 0 ? ReviewCategoryState.Partial : ReviewCategoryState.Ready,
                securityAssessed == 0 ? "No advisory source covers this inventory." : $"{s.AffectedDependencies} affected · {s.AdvisorySourceUnavailable} advisory source unavailable · {s.NoMatchedAdvisory} no matched advisory observed (not a safety claim)."),
            new("Deprecated / unlisted", registryAssessed == 0 ? ReviewCategoryState.NotAssessed : ReviewCategoryState.Ready,
                $"{items.Count(i => i.Registry.Deprecation is not null)} deprecated · {items.Count(i => i.Registry.State == RegistryState.Unlisted)} unlisted (NuGet terms). Deprecated is not vulnerable; unlisted is not malicious."),
            new("License metadata", denied > 0 ? ReviewCategoryState.NeedsReview : s.LicenseDetected == 0 ? ReviewCategoryState.NotAssessed : s.LicenseUnknown > 0 ? ReviewCategoryState.Partial : ReviewCategoryState.Ready,
                $"{s.LicenseDetected} detected · {s.LicenseUnknown} unknown or not declared ({(s.Dependencies == 0 ? 0 : 100 * s.LicenseDetected / s.Dependencies)}% available) · {(items.All(i => i.License.Policy == LicensePolicyState.NotConfigured) ? "policy not configured" : $"{denied} denied by policy")}."),
            new("Renovate policy", policySource is null ? ReviewCategoryState.NotAssessed : blocked > 0 ? ReviewCategoryState.NeedsReview : ReviewCategoryState.Ready,
                policySource is null ? "Not assessed — no source/config selected." : $"{remediations.Count} security fix version(s) checked against {policySource}; {blocked} need policy/manual action. The configuration is never changed."),
            new("Automation", automationEvidence.State switch { AutomationState.NotConfigured => ReviewCategoryState.NotConfigured, AutomationState.Observed => ReviewCategoryState.Ready, AutomationState.NotAssessed => ReviewCategoryState.NotAssessed, _ => ReviewCategoryState.Partial },
                $"{DependencyHealthLabels.Automation(automationEvidence.State)}. {automationEvidence.Detail}"),
        };
        var deployment = comparisons.FirstOrDefault(c => c.Kind == ComparisonKind.ExpectedVsDeployed);
        categories.Add(deployment is null
            ? new("Deployment comparison", ReviewCategoryState.NotAssessed, "Not assessed — no deployed evidence selected.")
            : new("Deployment comparison", deployment.Entries.All(e => e.State == ComparisonState.NotComparable) && deployment.Unchanged == 0 ? ReviewCategoryState.Partial : ReviewCategoryState.Ready,
                $"{deployment.Unchanged} matched · {Count(deployment, ComparisonState.DifferentVersion)} different version · {Count(deployment, ComparisonState.MissingInDeployment)} missing · {Count(deployment, ComparisonState.AdditionalInDeployment)} additional · {Count(deployment, ComparisonState.NotComparable)} not comparable (vs {deployment.RightName})."));
        if (comparisons.FirstOrDefault(c => c.Kind == ComparisonKind.SourceVsSbom) is { } cross)
            categories.Add(new("Source ↔ SBOM", Count(cross, ComparisonState.EvidenceConflict) > 0 ? ReviewCategoryState.NeedsReview : ReviewCategoryState.Ready,
                $"{cross.Unchanged} consistent · {Count(cross, ComparisonState.EvidenceConflict)} evidence conflict(s) · {Count(cross, ComparisonState.PresentNotDeclaredDirectly)} in SBOM not declared directly · {Count(cross, ComparisonState.DeclaredNotInSbom)} declared not in SBOM."));
        if (comparisons.FirstOrDefault(c => c.Kind == ComparisonKind.Baseline) is { } drift)
            categories.Add(new("Baseline drift", ReviewCategoryState.Ready,
                $"{Count(drift, ComparisonState.Added)} added · {Count(drift, ComparisonState.Removed)} removed · {Count(drift, ComparisonState.VersionChanged)} version changed · {Count(drift, ComparisonState.LicenseChanged)} license changed · {Count(drift, ComparisonState.SourceChanged)} source changed · {Count(drift, ComparisonState.MetadataChanged)} metadata changed (vs {drift.LeftName}). Changes are not defects."));
        return categories;
    }

    private static int Count(InventoryComparison comparison, ComparisonState state) => comparison.Entries.Count(e => e.State == state);

    private static string Label(InventoryDependency dep) => $"{dep.PackageName} {dep.Version ?? dep.VersionRange ?? "(version unknown)"}";

    public static List<HealthObservation> Observations(List<DependencyHealthItem> items, List<InventoryComparison> comparisons, InventoryFreshness freshness, string freshnessDetail, string? registry)
    {
        var list = new List<HealthObservation>();
        foreach (var item in items.Where(i => i.Security.State == AdvisoryState.Affected))
            foreach (var advisory in item.Security.Advisories.Where(a => a.AffectsObservedVersion == true && !a.Withdrawn))
                list.Add(new HealthObservation
                {
                    Id = $"advisory:{advisory.Id}:{item.Key}", Category = CheckCategory.Security, Kind = ObservationKind.Finding, Severity = DependencyFindingSeverity.NeedsReview, SourceSeverity = advisory.SourceSeverity,
                    PackageName = item.Dependency.PackageName, Title = $"{Label(item.Dependency)} is affected by {advisory.Id}",
                    Detail = $"{advisory.Summary} Severity per {advisory.Source}: {advisory.SourceSeverity ?? "not stated"}. Exploitability is not assessed.{(item.Security.FixedIn is { } f ? $" Fixed in {f} per the advisory source." : "")}",
                    Evidence = [.. advisory.Aliases, .. advisory.AffectedRanges.Select(r => $"Affected: {r}"), advisory.Url ?? "", item.Dependency.Location ?? ""],
                });
        foreach (var item in items.Where(i => i.Remediation?.State is RemediationPolicyState.BlockedByPolicy or RemediationPolicyState.BlockedByVersionConstraint or RemediationPolicyState.IgnoredByRenovate or RemediationPolicyState.RequiresApproval))
            list.Add(new HealthObservation
            {
                Id = $"remediation:{item.Key}", Category = CheckCategory.SourceConfiguration, Kind = ObservationKind.Finding, Severity = DependencyFindingSeverity.NeedsReview, PackageName = item.Dependency.PackageName,
                Title = $"{item.Dependency.PackageName}: {DependencyHealthLabels.Remediation(item.Remediation!.State)}", Detail = item.Remediation.Explanation,
                Evidence = [item.Remediation.PolicySource, .. item.Remediation.MatchedRules.Select(r => $"Rule #{r}")],
            });
        foreach (var comparison in comparisons.Where(c => c.Kind == ComparisonKind.SourceVsSbom))
            foreach (var conflict in comparison.Entries.Where(e => e.State == ComparisonState.EvidenceConflict))
                list.Add(new HealthObservation { Id = $"conflict:{conflict.PackageName}", Category = CheckCategory.Inventory, Kind = ObservationKind.EvidenceConflict, Severity = DependencyFindingSeverity.NeedsReview,
                    PackageName = conflict.PackageName, Title = $"Evidence conflict: {conflict.PackageName}", Detail = conflict.Detail, Evidence = [$"{comparison.LeftName}: {conflict.LeftValue}", $"{comparison.RightName}: {conflict.RightValue}"] });
        foreach (var item in items.Where(i => i.License.Policy == LicensePolicyState.Denied))
            list.Add(new HealthObservation { Id = $"license-denied:{item.Key}", Category = CheckCategory.License, Kind = ObservationKind.Finding, Severity = DependencyFindingSeverity.Warning,
                PackageName = item.Dependency.PackageName, Title = $"{item.Dependency.PackageName}: license on the configured deny list", Detail = item.License.Detail });
        var unavailable = items.Where(i => i.VersionStatus == VersionStatus.RegistryUnavailable).ToList();
        if (unavailable.Count > 0)
            list.Add(new HealthObservation { Id = "registry-unavailable", Category = CheckCategory.Registry, Kind = ObservationKind.Limitation, Title = $"Registry evidence unavailable for {unavailable.Count} dependenc{(unavailable.Count == 1 ? "y" : "ies")}",
                Detail = "No outdated/current conclusion is drawn for them. This does not mean the packages are missing.", Evidence = unavailable.GroupBy(i => i.Registry.State).Select(g => $"{DependencyHealthLabels.Registry(g.Key)}: {string.Join(", ", g.Select(i => i.Dependency.PackageName).Distinct().Take(10))}").ToList() });
        var notFound = items.Where(i => i.Registry.State == RegistryState.PackageNotFound).ToList();
        if (notFound.Count > 0)
            list.Add(new HealthObservation { Id = "registry-not-found", Category = CheckCategory.Registry, Kind = ObservationKind.Limitation, Title = $"{notFound.Select(i => i.Dependency.PackageName).Distinct().Count()} package(s) not found on {registry}",
                Detail = "Packages from a private feed are not visible: private registry metadata is not configured. Not found on the public registry is not absent from the deployment.", Evidence = notFound.Select(i => i.Dependency.PackageName).Distinct().Take(20).ToList() });
        var advisoryDown = items.Where(i => i.Security.State == AdvisoryState.AdvisorySourceUnavailable).ToList();
        if (advisoryDown.Count > 0)
            list.Add(new HealthObservation { Id = "advisory-unavailable", Category = CheckCategory.Security, Kind = ObservationKind.Limitation, Title = $"Security evidence unavailable for {advisoryDown.Count} dependenc{(advisoryDown.Count == 1 ? "y" : "ies")}",
                Detail = "The advisory source did not answer for them; this is not \"0 vulnerabilities\".", Evidence = advisoryDown.Select(i => i.Dependency.PackageName).Distinct().Take(20).ToList() });
        foreach (var item in items.Where(i => i.VersionStatus == VersionStatus.VersionUnavailable && i.Registry.State == RegistryState.VersionNotFound))
            list.Add(new HealthObservation { Id = $"version-not-found:{item.Key}", Category = CheckCategory.Registry, Kind = ObservationKind.Observed, PackageName = item.Dependency.PackageName,
                Title = $"{Label(item.Dependency)}: version not found on {registry}", Detail = item.Registry.Detail });
        foreach (var item in items.Where(i => i.Registry.Deprecation is not null))
            list.Add(new HealthObservation { Id = $"deprecated:{item.Key}", Category = CheckCategory.Registry, Kind = ObservationKind.Observed, PackageName = item.Dependency.PackageName,
                Title = $"{Label(item.Dependency)} is deprecated on {registry}", Detail = $"Reasons: {string.Join(", ", item.Registry.Deprecation!.Reasons)}.{(item.Registry.Deprecation.AlternatePackage is { } alt ? $" Alternate package: {alt}." : "")} Deprecated is not vulnerable." });
        foreach (var item in items.Where(i => i.Registry.State == RegistryState.Unlisted))
            list.Add(new HealthObservation { Id = $"unlisted:{item.Key}", Category = CheckCategory.Registry, Kind = ObservationKind.Observed, PackageName = item.Dependency.PackageName,
                Title = $"{Label(item.Dependency)} is unlisted on {registry}", Detail = "Unlisted is not malicious and not removed; the version still resolves." });
        var unknownLicense = items.Where(i => i.License.State is LicenseState.Unknown or LicenseState.NotDeclared).ToList();
        if (unknownLicense.Count > 0)
            list.Add(new HealthObservation { Id = "license-missing", Category = CheckCategory.License, Kind = ObservationKind.MissingEvidence,
                Severity = items.Any(i => i.License.Policy != LicensePolicyState.NotConfigured) ? DependencyFindingSeverity.Warning : DependencyFindingSeverity.Info,
                Title = $"License metadata unknown or not declared for {unknownLicense.Count} dependenc{(unknownLicense.Count == 1 ? "y" : "ies")}", Detail = "Missing evidence, not non-compliance.",
                Evidence = unknownLicense.Select(i => i.Dependency.PackageName).Distinct().Take(20).ToList() });
        if (freshness != InventoryFreshness.Current)
            list.Add(new HealthObservation { Id = "inventory-freshness", Category = CheckCategory.Inventory, Kind = ObservationKind.Limitation, Title = $"Inventory freshness: {freshness}", Detail = $"{freshnessDetail} Results describe this inventory, not the live repository or deployment." });
        foreach (var comparison in comparisons.Where(c => c.Kind == ComparisonKind.Baseline))
            foreach (var change in comparison.Entries.Where(e => e.State == ComparisonState.LicenseChanged))
                list.Add(new HealthObservation { Id = $"license-changed:{change.PackageName}", Category = CheckCategory.License, Kind = ObservationKind.Observed, PackageName = change.PackageName,
                    Title = $"License metadata changed: {change.PackageName}", Detail = $"{change.LeftValue} → {change.RightValue}. Not a policy violation unless a license policy says so." });
        return list;
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DependencyHealthRunSummary>> HistoryAsync(CancellationToken ct = default)
    {
        var records = await db.DependencyHealthRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Take(30).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<DependencyHealthRun>(r.ResultJson, Json)).OfType<DependencyHealthRun>()
            .Select(r => new DependencyHealthRunSummary(r.RunId, r.CompletedAt, r.Label, r.Inventory.Name, r.Summary.Dependencies,
                r.Observations.Count(o => o.Kind is ObservationKind.Finding or ObservationKind.EvidenceConflict), r.RefreshOf)).ToList();
    }

    /// <summary>The stored snapshot exactly as recorded — no registry or advisory source is queried when an old run is opened.</summary>
    public async Task<DependencyHealthRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var json = await db.DependencyHealthRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<DependencyHealthRun>(json, Json);
    }
}
