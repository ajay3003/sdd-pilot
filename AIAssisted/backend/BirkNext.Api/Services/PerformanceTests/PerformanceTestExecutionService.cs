using Path = System.IO.Path;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>Resolves the provider for a definition by stable id.</summary>
public sealed class PerformanceTestProviderRegistry(IEnumerable<IPerformanceTestProvider> providers, PerformanceTestOptions? options = null, PerformanceLocalExecutionStore? localExecution = null,
    IAiaCertificateFetcher? aia = null, Func<DateTimeOffset>? now = null)
{
    private readonly Dictionary<string, IPerformanceTestProvider> _byId = providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PerformanceProviderStatus Status)> _status = new();
    private readonly ConcurrentDictionary<string, PerformanceReachability> _reachability = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last container-network check for a provider + target origin.</summary>
    public PerformanceReachability? LastReachability(string providerId, string origin) => _reachability.GetValueOrDefault($"{providerId}|{origin}");

    /// <summary>Local data reset: forgets per-target reachability checks. Provider/runtime status (host capability) is kept.</summary>
    public void ClearReachability() => _reachability.Clear();

    public async Task<PerformanceReachability> CheckReachabilityAsync(PerformanceTestDefinition d, CancellationToken ct = default)
    {
        if (Find(d.ProviderId) is not { } provider)
            return new PerformanceReachability { TargetOrigin = d.TargetOrigin, State = "Unknown", Detail = $"No provider '{d.ProviderId}' is registered.", CheckedAt = DateTimeOffset.UtcNow };
        // Stamped with the network/TLS configuration it ran with: a later change (CA bundle, proxy, runtime, image, target) makes it stale.
        var result = await provider.CheckReachabilityAsync(d, ct) with { NetworkFingerprint = NetworkFingerprint(d) };
        var https = Uri.TryCreate(d.TargetOrigin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps;
        // A TLS failure in the container is explained from the host: the presented chain and the failing trust layer (never trusted, never sent).
        if (result.State is PerformanceProbeStates.TlsTrustFailure or PerformanceProbeStates.HostnameMismatch or PerformanceProbeStates.TlsFailure && https)
        {
            var capture = new TlsChainInspector.Capture();
            using var bundle = TlsChainInspector.LoadBundle(options?.Container.EffectiveCaBundlePath) is { } loaded ? new DisposableCollection(loaded) : null;
            var chain = await TlsChainInspector.InspectAsync(origin!, bundle?.Certificates, TimeSpan.FromSeconds(10), ct, capture: capture);
            result = result with { Chain = chain };
            if (chain.FailureClass == TlsFailureClasses.IncompleteServerChain && localExecution is not null)
                result = await RepairAsync(provider, d, origin!, result, chain, capture, ct);
        }
        else if (https && result.Reachable && localExecution?.RepairFor(origin!.Host, origin.Port) is { State: TlsRepairStates.Applied } repair)
        {
            // The check passed with the managed bundle. If the server now sends its complete chain, the workaround is retired and the target is
            // checked once more with its ordinary trust, so execution never depends on unnecessary repair state.
            var capture = new TlsChainInspector.Capture();
            var seen = await TlsChainInspector.InspectAsync(origin!, null, TimeSpan.FromSeconds(10), ct, capture: capture);
            if (seen.Reached && !seen.MissingIntermediate)
            {
                localExecution.RetireRepair(origin!.Host, origin.Port, TlsRepairStates.NoLongerNeeded,
                    "Server now provides the complete certificate chain: the local k6 trust repair is no longer used.");
                result = await provider.CheckReachabilityAsync(d, ct) with { NetworkFingerprint = NetworkFingerprint(d) };
                result = result with { TlsRepair = localExecution.RepairFor(origin.Host, origin.Port) };
            }
            else result = result with { TlsRepair = repair };
        }
        _reachability[$"{d.ProviderId}|{d.TargetOrigin}"] = result;
        // The same check validates Container TLS trust (System Settings → Performance Test Engines): one trust for the check and the run.
        localExecution?.Record(result);
        return result;
    }

    /// <summary>
    /// One automatic repair attempt for an incomplete server chain, then one revalidation — never a loop. Trusted roots = the k6 image's own default
    /// store + an approved configured CA bundle; only validated intermediates are added; the managed bundle (those roots + the intermediates) is used
    /// for this host's network check AND run. A revalidation that still fails retires the bundle (runtime stays blocked, with the reason).
    /// </summary>
    private async Task<PerformanceReachability> RepairAsync(IPerformanceTestProvider provider, PerformanceTestDefinition d, Uri origin, PerformanceReachability result,
        TlsChainDiagnostics chain, TlsChainInspector.Capture capture, CancellationToken ct)
    {
        var store = localExecution!;
        var at = now?.Invoke() ?? DateTimeOffset.UtcNow;
        TlsRepairOutcome Note(string state, string detail) => new() { State = state, Detail = detail, Host = origin.Host, Port = origin.Port };
        if (store.RepairMode != TlsRepairModes.Automatic)
            return result with { TlsRepair = Note(TlsRepairStates.Disabled, "Automatic repair is set to Manual only: nothing was retrieved. Fix the server chain, or allow automatic repair in System Settings → Performance Test Engines → TLS trust.") };
        if (aia is null || provider is not K6PerformanceTestProvider k6)
            return result with { TlsRepair = Note(TlsRepairStates.NotEligible, "Automatic repair is not available for this provider.") };
        var defaults = await k6.DefaultCaStorePemAsync(ct);
        if (defaults is null)
            return result with { TlsRepair = Note(TlsRepairStates.Stopped, "Automatic repair stopped: the k6 image's default CA store could not be read, so the trusted roots are unknown. Nothing was trusted.") };
        var roots = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        try
        {
            roots.ImportFromPem(defaults);
            var configured = options?.Container.EffectiveCaBundlePath is { } path && File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            if (configured is not null) roots.ImportFromPem(configured);
            var repaired = await TlsIntermediateRepair.RepairAsync(capture, origin.Host, origin.Port, roots, aia, at, ct);
            var outcome = repaired.Outcome;
            if (outcome.State != TlsRepairStates.Applied) return result with { TlsRepair = outcome, Chain = chain with { Repair = outcome } };
            var pem = new System.Text.StringBuilder(defaults.TrimEnd()).AppendLine();
            if (configured is not null) pem.AppendLine(configured.TrimEnd());
            foreach (var intermediate in repaired.Intermediates) { pem.AppendLine(intermediate.ExportCertificatePem()); intermediate.Dispose(); }
            store.SaveRepair(outcome, pem.ToString());
            // One revalidation from the container with the managed bundle (the same bundle the run will mount).
            var second = await provider.CheckReachabilityAsync(d, ct) with { NetworkFingerprint = NetworkFingerprint(d) };
            if (second.Reachable)
            {
                outcome = outcome with { RevalidationState = second.State };
                store.SaveRepair(outcome, pem.ToString());
                return second with { TlsRepair = outcome, Chain = chain with { Repair = outcome } };
            }
            store.RetireRepair(origin.Host, origin.Port, TlsRepairStates.Rejected,
                $"The k6 container still could not validate {origin.Host} with the repaired bundle ({PerformanceNetworkStages.Classification(second.State)}). Runtime execution stays blocked; fix the server chain.");
            var failed = store.RepairFor(origin.Host, origin.Port)! with { RevalidationState = second.State };
            return second with { NetworkFingerprint = NetworkFingerprint(d), TlsRepair = failed, Chain = (second.Chain ?? chain) with { Repair = failed } };
        }
        finally { foreach (var c in roots) c.Dispose(); }
    }

    private sealed class DisposableCollection(System.Security.Cryptography.X509Certificates.X509Certificate2Collection certificates) : IDisposable
    {
        public System.Security.Cryptography.X509Certificates.X509Certificate2Collection Certificates { get; } = certificates;
        public void Dispose() { foreach (var c in Certificates) c.Dispose(); }
    }

    /// <summary>The current network/TLS configuration fingerprint for a definition (see <see cref="PerformanceCaBundleInspector.NetworkFingerprint"/>).</summary>
    public string NetworkFingerprint(PerformanceTestDefinition d)
    {
        var container = options?.Container ?? new PerformanceContainerOptions();
        return PerformanceCaBundleInspector.NetworkFingerprint(d, container, PerformanceCaBundleInspector.Inspect(container.CaBundlePathFor(d.TargetOrigin)));
    }

    public void InvalidateStatus(string providerId) => _status.TryRemove(providerId, out _);

    public IReadOnlyCollection<IPerformanceTestProvider> All => _byId.Values;
    public IPerformanceTestProvider? Find(string providerId) => _byId.GetValueOrDefault(providerId);

    /// <summary>Provider status, cached for 30 s (detection starts a process).</summary>
    public async Task<PerformanceProviderStatus> StatusAsync(string providerId, CancellationToken ct = default)
    {
        if (Find(providerId) is not { } provider)
            return new PerformanceProviderStatus { ProviderId = providerId, DisplayName = providerId, Availability = ProviderAvailability.Unavailable, Detail = $"No performance test provider '{providerId}' is registered." };
        if (_status.TryGetValue(providerId, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(30)) return cached.Status;
        var status = await provider.StatusAsync(ct);
        _status[providerId] = (DateTimeOffset.UtcNow, status);
        return status;
    }
}

/// <summary>
/// Readiness of a definition: every prerequisite with an exact, tester-friendly reason. Never "Fail" — nothing has run. Thresholds, baseline
/// and observability are optional: without thresholds a run is measured, not assessed.
/// </summary>
public sealed class PerformanceTestReadinessService(PerformanceTestOptions options, PerformanceTestProviderRegistry providers, Resources.ResourceObservationRegistry? resources = null,
    IServiceScopeFactory? scopes = null, ContainerRuntime.IContainerExecutionRuntime? containers = null)
{
    /// <summary>
    /// The load generator's container limits: what is requested (memory, CPU, processes, read-only root with a small /tmp) and whether the runtime can
    /// enforce each (its delegated cgroup controllers). Never blocking and never Ready when a limit cannot be enforced — rootless Podman often
    /// delegates none, and the run then proceeds without them (BirkNext's load limits still apply). Whether a run applied them is the run's own evidence.
    /// </summary>
    private async Task<PerformanceReadinessItem?> ContainerLimitsItemAsync(CancellationToken ct)
    {
        if (containers is null) return null;
        const string Label = "Container resource limits";
        var c = options.Container;
        var requested = $"Requested: memory {c.MemoryMegabytes} MiB · {c.Cpus.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} CPU · {ContainerRuntime.PodmanContainerExecutionRuntime.PidsLimit} processes · read-only root file system with /tmp {ContainerRuntime.PodmanContainerExecutionRuntime.TmpfsMegabytes} MiB";
        IReadOnlySet<string> controllers;
        try { controllers = await containers.ControllersAsync(ct); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new("container-limits", Label, PerformanceReadinessState.Optional, $"{requested}. Whether the runtime can enforce them could not be determined.", false)
                { Category = PerformanceReadinessCategories.Optional, Summary = "container limits", Status = "Not assessed" };
        }
        var missing = new[] { ("memory", "memory"), ("cpu", "CPU"), ("pids", "process") }.Where(x => !controllers.Contains(x.Item1)).Select(x => x.Item2).ToList();
        var technical = controllers.Count == 0 ? "cgroup controllers: none delegated" : $"cgroup controllers: {string.Join(", ", controllers.Order(StringComparer.Ordinal))}";
        return missing.Count == 0
            ? new("container-limits", Label, PerformanceReadinessState.Ready, $"{requested}. The runtime delegates the memory, CPU and process controllers, so the limits can be enforced.", false)
                { Category = PerformanceReadinessCategories.Optional, Summary = "container limits", Status = "Enforceable", Technical = technical }
            : new("container-limits", Label, PerformanceReadinessState.Optional,
                $"{requested}. Not enforced: {string.Join(", ", missing)} limit{(missing.Count == 1 ? "" : "s")} — the runtime delegates no such cgroup controller (common for rootless Podman). BirkNext's load limits (users, rate, duration, requests) still apply.", false)
                { Category = PerformanceReadinessCategories.Optional, Summary = "container limits", Status = missing.Count == 3 ? "Not enforced" : "Partly enforced", Technical = technical };
    }

    /// <summary>Resource Stability readiness: provider and target status, policy, and whether the planned duration can give a steady-state trend at all.
    /// Never blocking unless the definition explicitly requires resource evidence — telemetry does not gate the load test.</summary>
    private async Task<List<PerformanceReadinessItem>> ResourceItemsAsync(PerformanceTestDefinition d, CancellationToken ct)
    {
        if (d.ResourceObservation is not { Enabled: true } r)
            return [new("resources", "Resource observation", PerformanceReadinessState.Optional, "Not configured — enable it to collect Resource Stability evidence (preferably with a soak workload).", false)];
        var items = new List<PerformanceReadinessItem>();
        if (resources is null) return [new("resources", "Resource observation", PerformanceReadinessState.Optional, "Resource observation providers are not available in this host.", false)];
        var statuses = await resources.TargetStatusAsync(d.EnvironmentId, d.EnvironmentType, ct);
        var selected = r.TargetComponentIds.Select(id => statuses.FirstOrDefault(s => s.Target.Id == id)).OfType<ResourceTargetStatus>().ToList();
        var allReady = selected.Count > 0 && selected.All(s => s.Availability == "Available");
        var anyUsable = selected.Any(s => s.Availability is "Available" or "Partial");
        var detail = selected.Count == 0 ? "No application component selected." : string.Join(" ", selected.Select(s => $"{s.Target.DisplayName}: {s.Availability} — {s.Detail}".Trim()));
        if (r.ObserveLoadGenerator) detail += " k6 load generator: observed separately as generator health.";
        items.Add(r.RequireResourceEvidence && !anyUsable
            ? new("resources", "Resource observation", PerformanceReadinessState.ProviderUnavailable, detail + " The definition requires resource evidence.", true)
            : new("resources", "Resource observation", allReady ? PerformanceReadinessState.Ready : PerformanceReadinessState.Optional,
                (allReady ? "" : "Resource evidence will be partial. ") + detail, false));
        var warmup = r.WarmupExclusionSeconds ?? d.Workload.WarmupSeconds + d.Workload.RampUpSeconds;
        var steady = Math.Max(0, d.Workload.TotalSeconds - warmup);
        var expected = r.SampleIntervalSeconds <= 0 ? 0 : steady / r.SampleIntervalSeconds;
        items.Add(expected < Resources.ResourceStabilityAnalyzer.MinimumSteadySamples
            ? new("resource-evidence", "Resource evidence duration", PerformanceReadinessState.Optional,
                $"About {expected} steady-state sample(s) after the {warmup} s warm-up exclusion: too few for a trend, so the run will report Insufficient evidence. Longer controlled runs (soak) give stronger resource-stability evidence.", false)
            : new("resource-evidence", "Resource evidence duration", PerformanceReadinessState.Ready,
                $"About {expected} steady-state samples over {(steady / 60.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} min every {r.SampleIntervalSeconds} s (warm-up {warmup} s excluded{(r.CooldownSeconds > 0 ? $", cooldown {r.CooldownSeconds} s observed" : "")}). {(d.Workload.Purpose == WorkloadPurpose.Soak ? "" : "A soak workload gives stronger evidence. ")}Short runs never prove that resources are stable or leaking.", false));
        items.Add(r.Policies.Count == 0
            ? new("resource-policy", "Resource policy", PerformanceReadinessState.Optional, "No resource policy: trends are shown descriptively (never passed or failed).", false)
            : new("resource-policy", "Resource policy", PerformanceReadinessState.Ready, $"{r.Policies.Count} stability polic{(r.Policies.Count == 1 ? "y" : "ies")}, {r.DriftPolicies.Count} drift polic{(r.DriftPolicies.Count == 1 ? "y" : "ies")}.", false));
        return items;
    }

    /// <param name="identity">The Target Environment's proxy identity (as a run start sends it): with it, the API credential a scenario uses is
    /// checked here exactly as at run start, so readiness is never Ready for a run that would be refused for a missing credential.</param>
    public async Task<PerformanceTestReadiness> EvaluateAsync(PerformanceTestDefinition d, PerformanceTestDataProfile? data, bool hasActiveBaseline, CancellationToken ct = default,
        BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity? identity = null)
    {
        // Endpoint Discovery evidence is read from the persisted history directly (never from what a browser page happened to load).
        var endpoint = await EndpointEvidenceAsync(d, ct);
        var issues = PerformanceTestSafety.Check(d, data, options, endpoint.ObservedAuthentication);
        var provider = await providers.StatusAsync(d.ProviderId, ct);
        var providerIssues = providers.Find(d.ProviderId)?.Validate(d) ?? [];
        var ca = PerformanceCaBundleInspector.Inspect(options.Container.EffectiveCaBundlePath);
        var items = new List<PerformanceReadinessItem>();
        const string Definition = PerformanceReadinessCategories.Definition, Setup = PerformanceReadinessCategories.ExecutionSetup,
            Safety = PerformanceReadinessCategories.Safety, Optional = PerformanceReadinessCategories.Optional;
        void Item(string key, string label, IEnumerable<PerformanceTestSafety.Issue> keyed, string okDetail, string category, string summary, string? action,
            PerformanceReadinessState okState = PerformanceReadinessState.Ready)
        {
            var found = keyed.ToList();
            var item = found.Count == 0 ? new PerformanceReadinessItem(key, label, okState, okDetail, false) : new(key, label, found[0].State, string.Join(" ", found.Select(f => f.Message)), true);
            // A production refusal is a safety blocker whatever item reports it.
            var hard = found.Any(f => f.State == PerformanceReadinessState.UnsafeEnvironment);
            items.Add(item with
            {
                Category = hard ? Safety : category, Summary = hard ? "production refused" : summary, Action = found.Count == 0 || hard ? null : action,
                Headline = found.Count == 0 ? "" : hard ? "Production target refused" : $"{label} needs attention",
            });
        }
        IEnumerable<PerformanceTestSafety.Issue> By(params string[] keys) => issues.Where(i => keys.Contains(i.Key));

        Item("target", "Target", By("target"), $"{d.TargetOrigin} (from the Target Environment)", Definition, "target", PerformanceReadinessActions.ConfigureTarget);
        Item("environment", "Environment and production guard", By("environment"), $"{d.EnvironmentName} ({d.EnvironmentType}) — a non-production environment; production is refused by the backend.",
            Safety, "production guard", null);
        // Runtime, image and provider: three separate tool states. None of them is a performance result.
        var runtime = provider.Runtime;
        if (runtime is not null)
            items.Add(runtime.Availability == ProviderAvailability.Available
                ? new("runtime", "Execution runtime", PerformanceReadinessState.Ready, runtime.Detail, false) { Category = Setup, Summary = "container runtime" }
                : new("runtime", "Execution runtime", PerformanceReadinessState.RuntimeUnavailable, $"{runtime.DisplayName}: {runtime.Detail}", true)
                    { Category = Setup, Summary = "container runtime", Headline = "The container runtime is not available", Action = PerformanceReadinessActions.OpenEngines, Owner = LocalExecution });
        if (provider.Image is not null && runtime?.Availability == ProviderAvailability.Available)
            items.Add(provider.ImagePresent
                ? new("image", "Provider image", PerformanceReadinessState.Ready, provider.ImageDigest is null ? provider.Image : $"{provider.Image} ({provider.ImageDigest[..Math.Min(19, provider.ImageDigest.Length)]}...)", false) { Category = Setup, Summary = "provider image" }
                : new("image", "Provider image", PerformanceReadinessState.ImageMissing, provider.Detail, true)
                    { Category = Setup, Summary = "provider image", Headline = "The k6 image is not available locally", Action = PerformanceReadinessActions.OpenEngines, Owner = LocalExecution });
        if (runtime?.Availability == ProviderAvailability.Available && await ContainerLimitsItemAsync(ct) is { } limits) items.Add(limits);
        var toolReady = provider.Availability == ProviderAvailability.Available;
        // An invalid CA bundle makes the provider "misconfigured"; that is reported once, as Corporate CA trust (its owner and key), not twice.
        var caBlocksProvider = ca is { Configured: true, Usable: false } && provider.Availability == ProviderAvailability.Misconfigured && providerIssues.Count == 0;
        if (!caBlocksProvider && (provider.Availability is not (ProviderAvailability.RuntimeUnavailable or ProviderAvailability.ImageMissing) || providerIssues.Count > 0))
            items.Add(toolReady && providerIssues.Count == 0
                ? new("provider", "Provider", PerformanceReadinessState.Ready, provider.Detail, false) { Category = Setup, Summary = "provider" }
                : new("provider", "Provider", PerformanceReadinessState.ProviderUnavailable, string.Join(" ", new[] { toolReady ? null : provider.Detail }.Concat(providerIssues).Where(t => t is not null)), true)
                    { Category = toolReady ? Definition : Setup, Summary = "provider", Headline = "The load-test provider cannot run this definition", Action = toolReady ? PerformanceReadinessActions.EditScenario : PerformanceReadinessActions.OpenEngines });

        // Container-network reachability: the load generator's own network, not the BirkNext host's (VPN, corporate DNS, private routes).
        var fingerprint = PerformanceCaBundleInspector.NetworkFingerprint(d, options.Container, PerformanceCaBundleInspector.Inspect(options.Container.CaBundlePathFor(d.TargetOrigin)));
        var reach = providers.LastReachability(d.ProviderId, d.TargetOrigin);
        var fresh = reach is not null && DateTimeOffset.UtcNow - reach.CheckedAt < TimeSpan.FromMinutes(options.Container.NetworkCheckValidMinutes);
        // A check made under another network/TLS configuration (CA bundle, proxy, runtime, image, target) is never reused.
        var sameConfiguration = reach is not null && reach.NetworkFingerprint == fingerprint;
        items.Add(NetworkItem(toolReady, reach, fresh, sameConfiguration));
        items.Add(CaTrustItem(ca, reach is not null && sameConfiguration ? reach : null, d));
        items.Add(new("tls", "TLS verification and proxy", PerformanceReadinessState.Ready,
            (ca.Usable ? "Configured corporate CA bundle (read-only)" : "Container default CA store")
            + "; TLS verification is always on and cannot be disabled." + (options.Container.HttpsProxy is null && options.Container.HttpProxy is null ? " No proxy." : " Allow-listed proxy settings only."), false)
            { Category = Safety, Summary = "TLS verification" });
        Item("scenario", "Scenario", By("scenario", "step-name", "method", "path", "header", "status", "think", "body", "graphql", "runtime", "dependency", "output", "variable"),
            $"{d.Scenario.Steps.Count} request step(s), read-only methods{(d.TargetType == PerformanceTargetType.GraphQlHttp ? ", GraphQL query only" : "")}.", Definition, "scenario", PerformanceReadinessActions.EditScenario);
        var auth = PerformanceScenarioRuntime.AuthenticationRequirements(d, endpoint.ObservedAuthentication);
        var intentional = auth.Where(r => r.State == StepAuthenticationState.IntentionallyUnauthenticated).ToList();
        var configured = auth.Count(r => r.State == StepAuthenticationState.Configured);
        Item("authentication", "Authentication", By("authentication"),
            intentional.Count > 0 && d.UnauthenticatedOverride is { } acknowledged
                ? $"Intentionally unauthenticated — acknowledged {acknowledged.AcknowledgedAt:yyyy-MM-dd HH:mm} UTC{(acknowledged.AcknowledgedBy is { } by ? $" by {by}" : "")} for {intentional.Count} operation(s) observed with authentication ({string.Join(", ", intentional.Select(r => r.Identity).Distinct())}). Responses may be 401/403 or follow a different code path."
                : configured > 0 ? $"Configured for {configured} step(s) that require authentication; the execution credential is checked separately."
                : endpoint.ObservedAuthentication is null && d.Scenario.Steps.Any(s => !s.Disabled) && endpoint.Item is { Key: "endpoint-evidence", Blocking: true }
                    ? "Not assessed — the authentication requirements could not be read."
                    : "No enabled step was observed or declared with authentication.",
            Definition, "authentication", PerformanceReadinessActions.EditScenario);
        if (PerformanceScenarioRuntime.UsesApiCredential(d) && scopes is not null)
        {
            // The same check the run start makes (present, in scope, valid for the whole run, this environment's own proxy identity).
            using var credentialScope = scopes.CreateScope();
            var problem = PerformanceApiCredential.Problem(credentialScope.ServiceProvider.GetService<LocalHttpsProxy.TransientAuthenticatedApiContextStore>(), d, d.EnvironmentId,
                identity, DateTimeOffset.UtcNow);
            items.Add(problem is null
                ? new("credential", "Execution credential", PerformanceReadinessState.Ready, "The Target Environment API credential captured by the Local HTTPS proxy is available for this run (its value is never shown).", false)
                    { Category = Setup, Summary = "execution credential" }
                : new("credential", "Execution credential", PerformanceReadinessState.NeedsAuthentication, problem, true)
                    { Category = Setup, Summary = "execution credential", Headline = "Authentication unresolved — execution blocked", Action = PerformanceReadinessActions.EditScenario, Owner = "Authentication" });
        }
        if (endpoint.Item is { } endpointItem) items.Add(endpointItem);
        var central = PerformanceTestSafety.UsesCentralTestData(d);
        Item("testdata", "Test data", By("testdata", "placeholder"),
            central ? $"{PerformanceCentralTestData.Mappings(d).Count()} mapping(s) to central Test Data (values stay in Test Data; resolved by the backend at run time)."
                : d.Scenario.TestDataProfileId is null ? "Not used." : $"Legacy local profile ({data?.Rows.Count ?? 0} rows) — not shared; prefer central Test Data.",
            central || d.Scenario.TestDataProfileId is not null ? Definition : Optional, "test data", PerformanceReadinessActions.EditScenario,
            central || d.Scenario.TestDataProfileId is not null ? PerformanceReadinessState.Ready : PerformanceReadinessState.Optional);
        await ScenarioEvidenceItemsAsync(d, items, ct);
        Item("workload", "Workload", By("workload"), $"{d.Workload.Purpose}, {d.Workload.Mode}.", Definition, "workload", PerformanceReadinessActions.EditWorkload);
        Item("limits", "Safety limits", By("limit-duration", "limit-purpose", "limit-vus", "limit-rate", "limit-requests"), "Within the configured safety limits.", Safety, "safety limits", PerformanceReadinessActions.EditWorkload);
        // A threshold no value can meet sensibly (negative, an error rate above 100 %, duplicated or contradictory) is never hidden until a run.
        var thresholdProblems = PerformanceTestRules.ThresholdProblems(d.Thresholds).Select((p, i) => p is null ? null : $"Threshold {i + 1}: {p}").OfType<string>().ToList();
        items.Add(thresholdProblems.Count > 0
            ? new("thresholds", "Thresholds", PerformanceReadinessState.InvalidScenario, string.Join(" ", thresholdProblems), true)
                { Category = Definition, Summary = "thresholds", Headline = "A threshold cannot be evaluated", Action = PerformanceReadinessActions.EditThresholds }
            : d.Thresholds.Count == 0
            ? new("thresholds", "Thresholds", PerformanceReadinessState.Optional, "No thresholds: the run will be measured, not assessed (no quality score).", false) { Category = Optional, Summary = "thresholds", Action = PerformanceReadinessActions.EditThresholds }
            : new("thresholds", "Thresholds", PerformanceReadinessState.Ready, $"{d.Thresholds.Count(t => t.Severity == ThresholdSeverity.Required)} required, {d.Thresholds.Count(t => t.Severity == ThresholdSeverity.Advisory)} advisory (configured expectations, not universal standards).", false) { Category = Definition, Summary = "thresholds" });
        items.Add(new("baseline", "Baseline", PerformanceReadinessState.Optional, hasActiveBaseline ? "An active baseline exists for this scope; drift will be assessed against it." : "No baseline selected — promote a completed run explicitly to create one.", false) { Category = Optional, Summary = "baseline" });
        items.Add(new("observability", "Observability", PerformanceReadinessState.Optional, "Optional — not configured. The run window is recorded so telemetry can be correlated later.", false) { Category = Optional, Summary = "observability" });
        if (d.ResourceObservation is { Enabled: true }) Item("resource-config", "Resource configuration", By("resources"), "Resource observation settings are valid.", Optional, "resource configuration", null);
        items.AddRange((await ResourceItemsAsync(d, ct)).Select(i => i with { Category = i.Blocking ? Setup : Optional, Summary = "resource observation" }));
        var blockers = items.Where(i => i.Blocking).Select(i => i.Detail).ToList();
        return new PerformanceTestReadiness
        {
            Identity = new PerformanceReadinessIdentity
            {
                DefinitionId = d.Id, DefinitionVersion = d.Version, EnvironmentId = d.EnvironmentId, TargetOrigin = d.TargetOrigin, ProviderId = d.ProviderId, RuntimeId = d.RuntimeId,
                NetworkFingerprint = fingerprint, EvaluatedAt = DateTimeOffset.UtcNow,
            },
            Ready = blockers.Count == 0, Items = items, Blockers = blockers, Limits = options.Limits(d.SafetyPolicy), EstimatedDurationSeconds = d.Workload.TotalSeconds,
            EstimatedMaxRequests = PerformanceTestSafety.EstimatedMaxRequests(d), Timeline = PerformanceTestRules.Timeline(d.Workload), Provider = provider,
        };
    }

    private const string LocalExecution = "Local execution configuration";

    /// <summary>Central Test Data mappings (approved, right type, right environment) and contract drift of declared steps — exact blockers
    /// ("Test Data missing for {personId}", "Contract changed · review required: request body changed"), never a generic "needs configuration".</summary>
    /// <summary>
    /// Endpoint evidence for the scenario's steps from the persisted Endpoint Discovery history of this environment and target host: which operation
    /// identities were observed with authentication (an authentication requirement), and — informational, never blocking — whether each discovered
    /// step's evidence is current, newer than when it was reviewed, historical, or no longer in the history. Manual steps claim no provenance.
    /// </summary>
    private async Task<(IReadOnlySet<string>? ObservedAuthentication, PerformanceReadinessItem? Item)> EndpointEvidenceAsync(PerformanceTestDefinition d, CancellationToken ct)
    {
        if (scopes is null || LocalHttpsProxy.EndpointObservationHistoryService.HostOf(d.TargetOrigin) is not { } host) return (null, null);
        BirkNext.LocalHttpsProxy.EndpointObservationHistoryView view;
        try
        {
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<LocalHttpsProxy.EndpointObservationHistoryService>() is not { } history) return (null, null);
            // The same view Endpoint Discovery and the Scenario tab show: persisted history plus a capture still running for this environment.
            view = await history.CurrentViewAsync(scope.ServiceProvider.GetService<LocalHttpsProxy.ILocalHttpsProxyService>(), d.EnvironmentId, host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown is not "none required": without the history the authentication requirements of the steps are not assessed, so readiness is
            // not Ready (retry when the history is readable again).
            return (null, new PerformanceReadinessItem("endpoint-evidence", "Endpoint evidence", PerformanceReadinessState.Blocked,
                "Endpoint Discovery history could not be read, so the authentication requirements of the steps are not assessed. Check readiness again.", true)
                { Category = PerformanceReadinessCategories.Definition, Summary = "endpoint evidence", Headline = "Authentication requirements not assessed", Status = "Not assessed" });
        }
        var observedAuth = view.Operations.Where(o => o.AuthObserved).Select(o => o.Identity).ToHashSet(StringComparer.Ordinal);
        var byIdentity = view.Operations.ToDictionary(o => o.Identity, StringComparer.Ordinal);
        var discovered = d.Scenario.Steps.Where(s => !s.Disabled && s.Source is { Origin: ScenarioOperationOrigin.Observed } or { Evidence: ScenarioEvidence.DeclaredAndObserved or ScenarioEvidence.ObservedOnly }).ToList();
        if (discovered.Count == 0) return (observedAuth, null);
        var states = discovered.Select(s => (Step: s, Info: PerformanceEndpointEvidence.Of(s, byIdentity.GetValueOrDefault(PerformanceScenarioRuntime.ObservationIdentity(s)), view.LatestRun, DateTimeOffset.UtcNow))).ToList();
        var newer = states.Count(e => e.Info.State == PerformanceEndpointEvidenceState.NewerAvailable);
        var missing = states.Count(e => e.Info.State == PerformanceEndpointEvidenceState.NotInHistory);
        // Per step, in the step card's own words (one shared summary): "Observed 3× · last observed 09:36 UTC · Follow latest observation".
        var detail = string.Join(" ", states.Select(e =>
            {
                var line = PerformanceEndpointEvidence.Summary(e.Info, byIdentity.GetValueOrDefault(e.Info.Identity)?.ObservationCount, PerformanceDiscoveryDrafts.TrackingLabel(PerformanceDiscoveryDrafts.Tracking(e.Step)));
                var comparison = PerformanceEndpointEvidence.Comparison(e.Info);
                return $"{(states.Count > 1 ? $"{e.Step.Name}: " : "")}{line}.{(comparison.Length > 0 ? $" {comparison}." : "")}";
            }))
            + (newer > 0 ? " Review latest values before running; reviewed values are never overwritten silently." : "")
            + (missing > 0 ? " A step not in the history keeps its reviewed values (it is not removed)." : "");
        return (observedAuth, new PerformanceReadinessItem("endpoint-evidence", "Endpoint evidence", newer + missing > 0 ? PerformanceReadinessState.Optional : PerformanceReadinessState.Ready, detail, false)
        {
            Category = PerformanceReadinessCategories.Definition, Summary = "endpoint evidence", Action = newer > 0 ? PerformanceReadinessActions.EditScenario : null,
            Status = newer > 0 ? "Newer observation available" : missing > 0 ? "Not in history" : "Observed",
        });
    }

    private async Task ScenarioEvidenceItemsAsync(PerformanceTestDefinition d, List<PerformanceReadinessItem> items, CancellationToken ct)
    {
        if (scopes is null) return;
        using var scope = scopes.CreateScope();
        if (PerformanceTestSafety.UsesCentralTestData(d) && scope.ServiceProvider.GetService<TestData.TestDataCatalog>() is { } catalog)
        {
            var issues = PerformanceCentralTestData.Issues(d, catalog, DateTimeOffset.UtcNow);
            items.Add(issues.Count == 0
                ? new("central-test-data", "Central Test Data", PerformanceReadinessState.Ready, "Every mapped asset exists, is approved, has the right data type and fits this environment.", false)
                    { Category = PerformanceReadinessCategories.Definition, Summary = "Test Data" }
                : new("central-test-data", "Central Test Data", PerformanceReadinessState.NeedsTestData, string.Join(" ", issues), true)
                    { Category = PerformanceReadinessCategories.Definition, Summary = "Test Data", Headline = issues[0], Action = PerformanceReadinessActions.EditScenario, Owner = "Test Data" });
        }
        if (scope.ServiceProvider.GetService<PerformanceScenarioSources>() is { } sources)
        {
            var drift = await sources.DriftAsync(d, ct);
            if (drift.Count > 0)
                items.Add(new("scenario-contract", "Scenario contract", PerformanceReadinessState.NeedsConfiguration, string.Join(" ", drift.Select(x => x.Text)), true)
                    { Category = PerformanceReadinessCategories.Definition, Summary = "contract changed", Headline = drift[0].Text, Action = PerformanceReadinessActions.EditScenario });
        }
    }

    /// <summary>The container network item. Host reachability never counts: only a check made from the load-generator container does.</summary>
    internal static PerformanceReadinessItem NetworkItem(bool checkable, PerformanceReachability? reach, bool fresh, bool sameConfiguration)
    {
        // Target reachability: the base target answered the k6 container. It never verifies a scenario endpoint (or its authentication).
        const string Label = "Target reachability", Summary = "target reachability", Category = PerformanceReadinessCategories.Reachability;
        if (!checkable)
            return new("network", Label, PerformanceReadinessState.Optional, "Checked once the provider is available.", false) { Category = Category, Summary = Summary };
        if (reach is null || !fresh || !sameConfiguration)
            return new("network", Label, PerformanceReadinessState.NeedsConfiguration,
                reach is not null && !sameConfiguration
                    ? "The network or TLS configuration changed since the last check (target, runtime, image, CA bundle or proxy): check the container network again."
                    : reach is not null ? "The last container network check has expired: check again before running."
                    : "The load generator must be able to reach the target independently of the BirkNext host. Host reachability does not prove container reachability.", true)
            {
                Category = Category, Summary = Summary, Action = PerformanceReadinessActions.CheckNetwork,
                Headline = reach is null ? "Container reachability has not been checked" : "Container reachability must be checked again",
                Status = reach is null ? "Not checked" : "Stale",
            };
        if (reach.Reachable)
            return new("network", Label, PerformanceReadinessState.Ready,
                $"{(reach.HttpStatus is { } httpStatus ? $"Base target returned HTTP {httpStatus} to the k6 container." : reach.Detail)} Scenario endpoints are not verified by this check. Checked {reach.CheckedAt.ToUniversalTime():HH:mm} UTC.", false)
                { Category = Category, Summary = Summary, Status = reach.State, Technical = reach.HttpStatus is { } h ? $"{reach.State} · HTTP {h}" : reach.State };
        // A TLS trust failure is reported by the Corporate CA trust item (an execution-setup blocker owned by local configuration), not as a network fault.
        var tlsTrust = PerformanceProbeStates.IsTlsTrust(reach);
        return new("network", Label, tlsTrust ? PerformanceReadinessState.Optional : PerformanceReadinessState.NetworkUnavailable, tlsTrust
                ? "The k6 container reached the target but cannot validate its certificate chain — see Container TLS trust for the cause and the recommended fix, then re-check the container network."
                : reach.Detail, !tlsTrust)
        {
            Category = Category, Summary = Summary, Action = PerformanceReadinessActions.CheckNetwork, Status = reach.State,
            Headline = ProbeHeadline(reach.State), Technical = reach.ErrorCode is { } code ? $"{reach.State} · code {code}" : reach.State,
        };
    }

    public static string ProbeHeadline(string state) => state switch
    {
        PerformanceProbeStates.DnsFailure => "DNS lookup failed from the k6 container",
        PerformanceProbeStates.Timeout => "The connection from the k6 container timed out",
        PerformanceProbeStates.ConnectionRefused => "The target refused the connection from the k6 container",
        PerformanceProbeStates.ConnectionFailed => "The k6 container could not connect to the target",
        PerformanceProbeStates.TlsTrustFailure => "TLS trust failed in the k6 container",
        PerformanceProbeStates.HostnameMismatch => "The target certificate does not match its host name",
        PerformanceProbeStates.TlsFailure => "The TLS handshake from the k6 container failed",
        PerformanceProbeStates.ProxyFailure => "The configured proxy failed for the k6 container",
        PerformanceProbeStates.RuntimeFailure => "The container runtime could not run the network check",
        PerformanceProbeStates.RuntimeUnavailable => "The container runtime is not available",
        PerformanceProbeStates.ImageMissing => "The k6 image is not available locally",
        _ => "The k6 container got no response from the target",
    };

    /// <summary>
    /// Corporate CA trust: Not required (the default store validated the target, or no HTTPS) · Ready (a valid bundle is configured) · Missing (the
    /// container does not trust the chain) · Invalid (a configured bundle is unusable) · Not assessed (no container check yet). Missing/Invalid
    /// block execution; there is never an insecure fallback.
    /// </summary>
    internal static PerformanceReadinessItem CaTrustItem(PerformanceCaBundleState ca, PerformanceReachability? reach, PerformanceTestDefinition d)
    {
        const string Label = "Container TLS trust", Summary = "container TLS trust", Category = PerformanceReadinessCategories.ExecutionSetup;
        var https = d.TargetOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var host = Uri.TryCreate(d.TargetOrigin, UriKind.Absolute, out var uri) ? uri.Host : d.TargetOrigin;
        var key = PerformanceCaBundleInspector.ConfigurationKey;
        if (ca is { Configured: true, Usable: false })
            return new("ca-trust", Label, PerformanceReadinessState.NeedsConfiguration, $"The configured corporate CA bundle cannot be used: {ca.Detail}", true)
            {
                Category = Category, Summary = Summary, Status = "Invalid", Headline = "The corporate CA bundle is invalid", Owner = LocalExecution, ConfigurationKey = key,
                Action = PerformanceReadinessActions.OpenLocalExecutionSetup, Technical = "TLS verification will not be disabled.",
            };
        // Failure-specific: an incomplete server chain, an expired or wrong-host certificate block runtime execution with the server-side fix
        // first — never "configure a CA bundle" as the recommended fix. Only runtime execution is blocked; designing the test is not.
        if (reach is { Chain: { FailureClass: not TlsFailureClasses.Other and not TlsFailureClasses.UntrustedPrivateOrCorporateCA } chain } && !reach.Reachable
            && reach.State is PerformanceProbeStates.TlsTrustFailure or PerformanceProbeStates.HostnameMismatch or PerformanceProbeStates.TlsFailure)
        {
            var incomplete = chain.FailureClass == TlsFailureClasses.IncompleteServerChain;
            var repair = reach.TlsRepair ?? chain.Repair;
            var detail = incomplete
                ? $"Execution blocked — target certificate chain is incomplete. The server does not send the intermediate certificate required by the k6 container to build a trusted TLS chain. {chain.Remediation}"
                : $"Execution blocked — {TlsFailureClasses.Label(chain.FailureClass).ToLowerInvariant()}. {chain.Remediation}";
            if (repair is { State: not TlsRepairStates.Applied }) detail += $" Automatic local repair: {repair.Detail}";
            return new("ca-trust", Label, PerformanceReadinessState.NeedsConfiguration, detail, true)
            {
                Category = Category, Summary = Summary, Status = "Blocked", Owner = LocalExecution,
                Headline = incomplete ? "Execution blocked — target certificate chain is incomplete" : $"Execution blocked — {TlsFailureClasses.Label(chain.FailureClass).ToLowerInvariant()}",
                Action = PerformanceReadinessActions.OpenLocalExecutionSetup, Technical = $"{chain.FailureClass} · {reach.State} · TLS verification will not be disabled.",
            };
        }
        if (reach is { Reachable: true, TlsRepair: { State: TlsRepairStates.Applied } applied })
            return new("ca-trust", Label, PerformanceReadinessState.Ready,
                $"Local k6 trust repair applied for {host}: the missing intermediate ({applied.IntermediateSubject}) was retrieved and validated against an existing trusted root. Server TLS configuration should still be corrected: the server chain is incomplete.", false)
            {
                Category = Category, Summary = Summary, Status = "Repaired locally", Owner = LocalExecution, Headline = "Server certificate chain is still incomplete",
                Action = PerformanceReadinessActions.OpenLocalExecutionSetup, Technical = $"Repaired bundle {applied.BundleSha256} · validated {applied.ValidatedAt:yyyy-MM-dd HH:mm} UTC.",
            };
        if (reach is not null && PerformanceProbeStates.IsTlsTrust(reach))
            return new("ca-trust", Label, PerformanceReadinessState.NeedsConfiguration, ca.Usable
                    ? $"The configured CA bundle does not make the certificate chain for {host} trusted in the k6 container. Add the issuing corporate CA to the bundle."
                    : $"The k6 container cannot validate the certificate chain for {host}: the target TLS certificate is not trusted by the default k6 container CA store. Configure the corporate CA bundle.", true)
            {
                Category = Category, Summary = Summary, Status = "Missing", Headline = "Container TLS trust is not configured", Owner = LocalExecution, ConfigurationKey = key,
                Action = PerformanceReadinessActions.OpenLocalExecutionSetup, Technical = $"{reach.State} · code {reach.ErrorCode?.ToString() ?? "1310"} · TLS verification will not be disabled.",
            };
        if (ca.Usable)
            return new("ca-trust", Label, PerformanceReadinessState.Ready, ca.Detail, false) { Category = Category, Summary = Summary, Status = "Ready", Owner = LocalExecution, ConfigurationKey = key };
        if (!https)
            return new("ca-trust", Label, PerformanceReadinessState.Optional, "Not required: the target does not use HTTPS.", false) { Category = Category, Summary = Summary, Status = "Not required" };
        if (reach is { Reachable: true })
            return new("ca-trust", Label, PerformanceReadinessState.Ready, $"Not required: the k6 container validated the certificate chain for {host} with its default CA store.", false)
                { Category = Category, Summary = Summary, Status = "Not required" };
        return new("ca-trust", Label, PerformanceReadinessState.Optional,
            "Not assessed: the container network check shows whether the target certificate chain needs a corporate CA bundle.", false)
            { Category = Category, Summary = Summary, Status = "Not assessed", Owner = LocalExecution, ConfigurationKey = key };
    }
}

public sealed record PerformanceRunStartResult(PerformanceTestRun? Run, List<string> Blockers, string? Conflict);

/// <summary>
/// Executes performance tests: re-validates readiness and safety on the backend, allows one active run per environment, runs the provider in
/// the background, honours cancellation and timeout, evaluates BirkNext thresholds and drift, and persists the immutable run.
/// </summary>
public sealed class PerformanceTestExecutionService(IServiceScopeFactory scopes, PerformanceTestOptions options, PerformanceTestProviderRegistry providers,
    ILogger<PerformanceTestExecutionService> logger, TimeProvider? clock = null, Resources.ResourceObservationRegistry? resources = null)
{
    /// <summary>The source snapshot a definition's scenario was derived from: its steps' source, else the scenario's contract snapshot; null for a
    /// hand-written scenario. Run provenance — never the newest workspace snapshot at run time.</summary>
    public static Guid? ProvenanceSnapshot(PerformanceTestDefinition definition) =>
        definition.Scenario.Steps.Select(step => step.Source?.SnapshotId).FirstOrDefault(id => id is not null) ?? definition.Scenario.ContractSnapshotId;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    /// <summary>Cancels every active run and waits (bounded) for each to stop its provider container and resource collectors. Used at API shutdown.</summary>
    public async Task CancelAllAsync(TimeSpan wait)
    {
        foreach (var tracker in _trackers.Values) tracker.RequestStop(PerformanceStopReason.ApplicationShutdown, "BirkNext is shutting down.");
        foreach (var cts in _active.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        var tasks = _tasks.Values.ToArray();
        if (tasks.Length > 0) { try { await Task.WhenAll(tasks).WaitAsync(wait); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { } }
    }

    /// <summary>Starts resource collection for the run (null when not configured). Approved targets only; the k6 container is load-generator health.</summary>
    private Resources.ResourceObservationSession? StartResourceSession(PerformanceTestRun run, Func<ResourceStabilityAssessment, Task> persist)
    {
        if (resources is null || run.DefinitionSnapshot.ResourceObservation is not { Enabled: true } config) return null;
        var d = run.DefinitionSnapshot;
        var components = config.TargetComponentIds.Distinct(StringComparer.Ordinal)
            .Select(id => resources.Resolve(id, d.EnvironmentId, d.EnvironmentType)).OfType<Resources.ResourceTargetSpec>()
            .Select(spec => (spec, resources.Provider(spec.Target.ProviderId))).ToList();
        if (config.ObserveLoadGenerator)
        {
            var generator = Resources.ResourceObservationRegistry.LoadGenerator(run.RunId);
            components.Add((generator, resources.Provider(generator.Target.ProviderId)));
        }
        var o = resources.Options;
        var interval = TimeSpan.FromSeconds(Math.Clamp(config.SampleIntervalSeconds, o.MinSampleIntervalSeconds, o.MaxSampleIntervalSeconds));
        var session = new Resources.ResourceObservationSession(run.RunId, components, interval, o.MaxSamplesPerComponent, logger) { OnRound = persist };
        session.Start();
        return session;
    }

    /// <summary>Assessment of the collected evidence over the run's windows (warm-up excluded, cooldown separate).</summary>
    private ResourceStabilityAssessment? AssessResources(PerformanceTestRun run, Resources.ResourceObservationSession? session, DateTimeOffset workloadStart, DateTimeOffset workloadEnd)
    {
        if (session is null || run.DefinitionSnapshot.ResourceObservation is not { } config) return null;
        var w = run.DefinitionSnapshot.Workload;
        var warmup = Math.Max(0, config.WarmupExclusionSeconds ?? w.WarmupSeconds + w.RampUpSeconds);
        var windows = new Resources.ResourceWindows(workloadStart, workloadEnd, warmup, Math.Max(0, config.CooldownSeconds));
        return Resources.ResourceStabilityAnalyzer.Assess(session.Snapshot(inProgress: false) with
        {
            PolicyFingerprint = PerformanceTestStore.Fingerprint(new { config.Policies, config.DriftPolicies, warmup, config.SampleIntervalSeconds }),
        }, config, windows);
    }
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<Guid, RunTracker> _trackers = new();

    /// <summary>
    /// The one live record of an executing run. Every in-flight save (lifecycle, resource rounds, live execution evidence, cancel) goes through
    /// it, serialised, so concurrent writers never overwrite each other's fields; once the run is finished, a late update is a no-op (the store
    /// also refuses to change a finished run). The stop reason is set once, by whoever observed the cause first (user, watchdog, shutdown).
    /// </summary>
    private sealed class RunTracker(PerformanceTestRun run, IServiceScopeFactory scopes, TimeProvider clock)
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _save = new(1, 1);
        private PerformanceTestRun _run = run with { ExecutionEvidence = run.ExecutionEvidence ?? new PerformanceExecutionEvidence() };
        private bool _finished;

        public PerformanceTestRun Current { get { lock (_gate) return _run; } }
        public PerformanceExecutionEvidence Evidence => Current.ExecutionEvidence!;
        public void Update(Func<PerformanceTestRun, PerformanceTestRun> change) { lock (_gate) _run = change(_run); }
        public void Evidence_(Func<PerformanceExecutionEvidence, PerformanceExecutionEvidence> change) => Update(r => r with { ExecutionEvidence = change(r.ExecutionEvidence ?? new()) });
        public void Advance(PerformanceRunStage stage) => Evidence_(e => PerformanceLiveMonitor.Advance(e, stage, clock.GetUtcNow()));
        public void Event(string name, string? detail = null) => Evidence_(e => PerformanceLiveMonitor.Event(e, clock.GetUtcNow(), name, detail));

        public bool RequestStop(PerformanceStopReason reason, string detail)
        {
            lock (_gate)
            {
                var e = _run.ExecutionEvidence ?? new();
                if (_finished || e.StopReason != PerformanceStopReason.None) return false;
                var now = clock.GetUtcNow();
                _run = _run with { ExecutionEvidence = PerformanceLiveMonitor.Event(e with { StopReason = reason, StopRequestedAt = now, StopDetail = detail }, now,
                    PerformanceExecutionAssessment.StopReasonLabel(reason), detail) };
                return true;
            }
        }

        public async Task<PerformanceTestRun> PersistAsync()
        {
            await _save.WaitAsync();
            try
            {
                if (_finished) return Current;
                using var scope = scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<PerformanceTestStore>().SaveRunAsync(Current, CancellationToken.None);
            }
            catch (InvalidOperationException) { _finished = true; return Current; }
            finally { _save.Release(); }
        }

        /// <summary>The single terminal write; afterwards every update is ignored (no second terminal outcome, no late overwrite).</summary>
        public async Task<PerformanceTestRun> FinishAsync(Func<PerformanceTestStore, PerformanceTestRun, Task<PerformanceTestRun>> finish)
        {
            await _save.WaitAsync();
            try
            {
                if (_finished) return Current;
                using var scope = scopes.CreateScope();
                var done = await finish(scope.ServiceProvider.GetRequiredService<PerformanceTestStore>(), Current);
                lock (_gate) { _run = done; _finished = true; }
                return done;
            }
            finally { _save.Release(); }
        }
    }

    /// <summary>
    /// Live execution evidence while the provider runs: observes the run container through the runtime and reads the provider's own telemetry
    /// stream (k6 JSON output), applies <see cref="PerformanceLiveMonitor"/> and persists. The watchdog stops the run with its own reason —
    /// never as a user cancellation.
    /// </summary>
    private async Task MonitorAsync(RunTracker tracker, IPerformanceTestProvider provider, IPerformanceTelemetryReader? reader, Guid runId, DateTimeOffset providerStartedAt, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.LiveMonitorSeconds));
        var containerInstrumented = true;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stop); } catch (OperationCanceledException) { return; }
            PerformanceContainerObservation? observed = null;
            try
            {
                observed = await provider.ObserveAsync(runId, stop);
                if (observed is null) containerInstrumented = false;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { logger.LogDebug("Run {RunId}: container observation failed ({Type}).", runId, ex.GetType().Name); }
            var telemetry = reader?.Read();
            PerformanceLiveMonitor.WatchdogStop? watchdog = null;
            tracker.Evidence_(e =>
            {
                var (next, decision) = PerformanceLiveMonitor.Apply(e, _clock.GetUtcNow(), providerStartedAt, observed, containerInstrumented, telemetry, options);
                watchdog = decision;
                return next;
            });
            if (watchdog is { } w && tracker.RequestStop(w.Reason, w.Detail) && _active.TryGetValue(runId, out var cts))
            {
                logger.LogWarning("Performance test {RunId} stopped by the watchdog: {Reason}.", runId, w.Reason);
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            await tracker.PersistAsync();
        }
    }

    /// <summary>True while a run is executing in this process (the reset refuses to run under an active writer).</summary>
    public bool HasActiveRuns => !_active.IsEmpty;
    private readonly ConcurrentDictionary<string, Guid> _activeByEnvironment = new(StringComparer.Ordinal);
    private int _orphansCleaned;

    /// <summary>Removes stale BirkNext-managed provider containers (labels only; never other containers). Once per process, and on demand.</summary>
    public async Task<IReadOnlyList<string>> CleanupOrphansAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && Interlocked.Exchange(ref _orphansCleaned, 1) == 1) return [];
        var removed = new List<string>();
        var active = _active.Keys.ToHashSet();
        foreach (var provider in providers.All)
        {
            try { removed.AddRange(await provider.CleanupOrphansAsync(active, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Orphan cleanup failed for {Provider}: {Type}", provider.ProviderId, ex.GetType().Name); }
        }
        return removed;
    }

    /// <summary>The background task of the last started run (tests await it).</summary>
    internal Task? LastExecution { get; private set; }

    public async Task<PerformanceRunStartResult> StartAsync(string environmentId, PerformanceRunRequest request, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PerformanceTestStore>();
        var readinessService = scope.ServiceProvider.GetRequiredService<PerformanceTestReadinessService>();
        var definition = await store.DefinitionAsync(environmentId, request.DefinitionId, ct);
        if (definition is null) return new(null, ["The performance test definition does not exist in this environment."], null);
        if (definition.Archived) return new(null, ["The definition is archived; restore or copy it to run."], null);
        var data = await store.DataProfileAsync(environmentId, definition.Scenario.TestDataProfileId, ct);
        var baseline = await store.ActiveBaselineAsync(environmentId, definition.Id, definition.ComparisonFingerprint, ct);
        var readiness = await readinessService.EvaluateAsync(definition, PerformanceTestSafety.HasRuntimeMappings(definition) ? null : data, baseline is not null, ct, request.Identity);
        if (!readiness.Ready) return new(null, readiness.Blockers, null);
        // Target Environment API credential: present, in scope and valid for the whole run, from this environment's own proxy identity.
        var credentialStore = scope.ServiceProvider.GetService<LocalHttpsProxy.TransientAuthenticatedApiContextStore>();
        if (PerformanceApiCredential.Problem(credentialStore, definition, environmentId, request.Identity, _clock.GetUtcNow()) is { } credentialProblem)
            return new(null, [credentialProblem], null);
        // Central Test Data: resolved here, by the backend, for this run only (a transient profile); the definition and the run keep aliases only.
        if (PerformanceTestSafety.HasRuntimeMappings(definition)) data = null;
        if (PerformanceTestSafety.UsesCentralTestData(definition))
        {
            if (scope.ServiceProvider.GetService<TestData.TestDataCatalog>() is not { } catalog || PerformanceCentralTestData.Resolve(definition, catalog) is not { } resolved)
                return new(null, ["Central Test Data could not be resolved for this run."], null);
            data = resolved;
        }
        await ReconcileAsync(store, environmentId, ct);
        await CleanupOrphansAsync(false, ct);
        if (_activeByEnvironment.ContainsKey(environmentId) || await store.HasActiveRunAsync(environmentId, ct))
            return new(null, [], "A performance test is already running for this environment. Wait for it to finish or cancel it.");

        var now = _clock.GetUtcNow();
        // Provenance is the source snapshot the scenario was derived from (its steps' source, else the scenario's contract snapshot), so a run
        // stays reproducible — never "whichever snapshot is newest in the workspace at run time". Hand-written scenarios have none.
        var sourceSnapshot = ProvenanceSnapshot(definition);
        var run = new PerformanceTestRun
        {
            EnvironmentId = environmentId, ProjectId = definition.ProjectId, DefinitionId = definition.Id, DefinitionVersion = definition.Version,
            DefinitionFingerprint = definition.Fingerprint, ComparisonFingerprint = definition.ComparisonFingerprint, DefinitionSnapshot = definition,
            ProviderId = definition.ProviderId, ProviderVersion = readiness.Provider?.Version, TargetOrigin = definition.TargetOrigin, EnvironmentType = definition.EnvironmentType,
            RuntimeId = readiness.Provider?.Runtime?.RuntimeId, RuntimeVersion = readiness.Provider?.Runtime?.Version, ContainerImage = readiness.Provider?.Image, ImageDigest = readiness.Provider?.ImageDigest,
            CreatedAt = now, State = PerformanceRunState.Queued, BaselineIdAtRun = baseline?.BaselineId,
            VersionLabel = string.IsNullOrWhiteSpace(request.VersionLabel) ? null : request.VersionLabel.Trim()[..Math.Min(100, request.VersionLabel.Trim().Length)],
            SourceSnapshotId = sourceSnapshot,
            Host = new PerformanceExecutionHost(RuntimeInformation.OSDescription, Environment.ProcessorCount, typeof(PerformanceTestExecutionService).Assembly.GetName().Version?.ToString() ?? "unknown"),
        };
        if (!_activeByEnvironment.TryAdd(environmentId, run.RunId)) return new(null, [], "A performance test is already running for this environment.");
        run = run with { ExecutionEvidence = PerformanceLiveMonitor.Event(new PerformanceExecutionEvidence { StageChangedAt = now }, now, "Queued", $"readiness re-evaluated by the backend ({readiness.Items.Count} checks)") };
        _trackers[run.RunId] = new RunTracker(run, scopes, _clock);
        // Registered as executing in this process before the Queued row is visible: a concurrent read must never reconcile a run that is starting.
        var cts = new CancellationTokenSource();
        _active[run.RunId] = cts;
        try { await store.SaveRunAsync(run, ct); }
        catch { _activeByEnvironment.TryRemove(environmentId, out _); _trackers.TryRemove(run.RunId, out _); _active.TryRemove(run.RunId, out _); cts.Dispose(); throw; }
        var identity = request.Identity;
        LastExecution = Task.Run(() => ExecuteAsync(run, data, cts.Token, identity));
        _tasks[run.RunId] = LastExecution;
        return new(run, [], null);
    }

    public async Task<PerformanceTestRun?> CancelAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PerformanceTestStore>();
        var run = await store.RunAsync(runId, ct);
        if (run is null) return null;
        if (!run.IsActive) return run;
        if (_active.TryGetValue(runId, out var cts))
        {
            if (_trackers.TryGetValue(runId, out var tracker))
            {
                tracker.RequestStop(PerformanceStopReason.CancelledByUser, "Cancel requested by the user.");
                tracker.Update(r => r with { State = PerformanceRunState.Cancelling, StateReason = "Cancellation requested by the user." });
                var cancelling = await tracker.PersistAsync();
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                return cancelling;
            }
            var requested = run with { State = PerformanceRunState.Cancelling, StateReason = "Cancellation requested by the user." };
            await store.SaveRunAsync(requested, ct);
            cts.Cancel();
            return requested;
        }
        return await ReconcileRunAsync(store, run, ct);
    }

    /// <summary>A run left active by a BirkNext restart can never finish: it becomes ExecutionFailed (not a quality result).</summary>
    public async Task ReconcileAsync(PerformanceTestStore store, string environmentId, CancellationToken ct = default)
    {
        // Every active run, however old: a run interrupted before the 20 most recent must not stay "Running" forever.
        foreach (var run in (await store.ActiveRunsAsync(environmentId, ct)).Where(r => r.IsActive && !_active.ContainsKey(r.RunId)))
            await ReconcileRunAsync(store, run, ct);
    }

    private async Task<PerformanceTestRun> ReconcileRunAsync(PerformanceTestStore store, PerformanceTestRun run, CancellationToken ct)
    {
        if (_active.ContainsKey(run.RunId)) return run;
        var now = _clock.GetUtcNow();
        var evidence = run.ExecutionEvidence is { } e
            ? PerformanceLiveMonitor.Event(e with { Stage = PerformanceRunStage.Finished, StageChangedAt = now, StopReason = e.StopReason == PerformanceStopReason.None ? PerformanceStopReason.ApplicationShutdown : e.StopReason,
                Telemetry = e.Telemetry == PerformanceTelemetryState.Receiving ? PerformanceTelemetryState.Unavailable : e.Telemetry }, now,
                "Interrupted / orphaned", "BirkNext stopped while the run was active; evidence recorded before that is kept, nothing after it is known.")
            : null;
        var failed = run with { State = PerformanceRunState.ExecutionFailed, StateReason = "Interrupted — BirkNext stopped while the run was active; the provider result is unknown.", FinishedAt = now,
            ThresholdResults = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, null, false), ExecutionEvidence = evidence };
        return await store.SaveRunAsync(failed, ct);
    }

    /// <summary>One run as stored, reconciled first when it claims to be active but no longer executes in this process (polling never shows a stale "Running").</summary>
    public async Task<PerformanceTestRun?> CurrentRunAsync(PerformanceTestStore store, Guid runId, CancellationToken ct = default)
    {
        var run = await store.RunAsync(runId, ct);
        return run is { IsActive: true } && !_active.ContainsKey(runId) ? await ReconcileRunAsync(store, run, ct) : run;
    }

    private async Task ExecuteAsync(PerformanceTestRun queued, PerformanceTestDataProfile? data, CancellationToken ct, BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity? identity = null)
    {
        var tracker = _trackers.GetOrAdd(queued.RunId, _ => new RunTracker(queued, scopes, _clock));
        var run = queued;
        Resources.ResourceObservationSession? session = null;
        DateTimeOffset? workloadStart = null, workloadEnd = null;
        using var monitorStop = new CancellationTokenSource();
        Task? monitor = null;
        async Task StopMonitorAsync()
        {
            monitorStop.Cancel();
            if (monitor is not null) { try { await monitor; } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
        }
        try
        {
            tracker.Update(r => r with { State = PerformanceRunState.Preparing });
            tracker.Advance(PerformanceRunStage.Preparing);
            run = await tracker.PersistAsync();
            // Safety re-checked immediately before the provider starts (the definition snapshot, not whatever the UI shows).
            var blockers = PerformanceTestSafety.Check(run.DefinitionSnapshot, data, options);
            if (blockers.Count > 0)
            {
                await tracker.FinishAsync((store, r) => store.SaveRunAsync(r with { State = PerformanceRunState.Blocked, StateReason = string.Join(" ", blockers.Select(b => b.Message)), FinishedAt = _clock.GetUtcNow(),
                    ThresholdResults = PerformanceTestRules.EvaluateThresholds(r.DefinitionSnapshot.Thresholds, null, false), ExecutionEvidence = Ended(r.ExecutionEvidence, "Blocked before start") }, CancellationToken.None));
                return;
            }
            var provider = providers.Find(run.ProviderId)!;
            // A fresh container-network check immediately before the load starts: an unreachable target is Blocked (a precondition), never a result.
            var reach = await providers.CheckReachabilityAsync(run.DefinitionSnapshot, ct);
            if (!reach.Reachable)
            {
                await tracker.FinishAsync((store, r) => store.SaveRunAsync(r with { State = PerformanceRunState.Blocked, StateReason = reach.Detail, FinishedAt = _clock.GetUtcNow(), Reachability = reach,
                    ThresholdResults = PerformanceTestRules.EvaluateThresholds(r.DefinitionSnapshot.Thresholds, null, false), ExecutionEvidence = Ended(r.ExecutionEvidence, "Blocked — target unreachable from the container network") }, CancellationToken.None));
                return;
            }
            // The captured API credential is leased only now, for this container, and re-checked (it may have expired or been replaced since the start).
            LeasedApiCredential? credential = null;
            if (PerformanceScenarioRuntime.UsesApiCredential(run.DefinitionSnapshot))
            {
                using var credentialScope = scopes.CreateScope();
                var (leased, leaseProblem) = PerformanceApiCredential.Lease(credentialScope.ServiceProvider.GetService<LocalHttpsProxy.TransientAuthenticatedApiContextStore>(),
                    run.DefinitionSnapshot, run.EnvironmentId, identity, _clock.GetUtcNow());
                if (leased is null)
                {
                    await tracker.FinishAsync((store, r) => store.SaveRunAsync(r with { State = PerformanceRunState.Blocked, StateReason = leaseProblem, FinishedAt = _clock.GetUtcNow(),
                        ThresholdResults = PerformanceTestRules.EvaluateThresholds(r.DefinitionSnapshot.Thresholds, null, false), ExecutionEvidence = Ended(r.ExecutionEvidence, "Blocked — API credential unavailable") }, CancellationToken.None));
                    return;
                }
                credential = leased;
            }
            var startedAt = _clock.GetUtcNow();
            // "Running" (the persisted state) means the provider was started — the stage and evidence below say how far execution actually got.
            tracker.Update(r => r with { State = PerformanceRunState.Running, StartedAt = startedAt, Reachability = reach });
            tracker.Evidence_(e => e with { ExpectedWorkloadEndAt = startedAt.AddSeconds(run.DefinitionSnapshot.Workload.TotalSeconds), Container = PerformanceObservedState.NotStarted });
            tracker.Advance(PerformanceRunStage.StartingContainer);
            run = await tracker.PersistAsync();
            // Resource observation starts with the workload; partial evidence is persisted after each round so it survives a cancel or a restart.
            session = StartResourceSession(run, async snapshot =>
            {
                tracker.Update(r => r with { Resources = snapshot });
                await tracker.PersistAsync();
            });
            workloadStart = run.StartedAt;
            var timeout = TimeSpan.FromSeconds(run.DefinitionSnapshot.Workload.TotalSeconds + options.ProviderTimeoutGraceSeconds);
            var workDir = Path.Combine(Path.GetTempPath(), "birknext-performance", run.RunId.ToString("N"));
            var input = new PerformanceProviderInput(run.RunId, run.DefinitionSnapshot, data, workDir, timeout) { Credential = credential };
            var reader = provider.OpenTelemetry(input);
            tracker.Evidence_(e => e with
            {
                Telemetry = reader is null ? PerformanceTelemetryState.NotInstrumented : PerformanceTelemetryState.NotYetReceived, TelemetrySource = reader?.Source,
            });
            monitor = MonitorAsync(tracker, provider, reader, run.RunId, startedAt, monitorStop.Token);
            var result = await provider.ExecuteAsync(input, null, ct);
            await StopMonitorAsync();
            workloadEnd = _clock.GetUtcNow();
            tracker.Advance(PerformanceRunStage.CollectingResults);
            // Optional post-load cooldown: does the component return toward its steady state once the load stops? Cancelling here keeps the result.
            if (session is not null && result.State == PerformanceRunState.Completed && run.DefinitionSnapshot.ResourceObservation?.CooldownSeconds is > 0 and var cooldown)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(cooldown, resources!.Options.MaxCooldownSeconds)), ct); }
                catch (OperationCanceledException) { }
            }
            if (session is not null) await session.StopAsync();
            var assessment = AssessResources(tracker.Current, session, workloadStart.Value, workloadEnd.Value);
            run = await tracker.FinishAsync((store, r) => FinishAsync(store, r, result, assessment));
            logger.LogInformation("Performance test {RunId} for {EnvironmentId}: {State}, verdict {Verdict}, {Requests} request(s).", run.RunId, run.EnvironmentId, run.State, run.Verdict, run.Metrics?.RequestCount ?? 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopMonitorAsync();
            if (session is not null) await session.StopAsync();
            tracker.RequestStop(PerformanceStopReason.Unknown, "Stopped without a recorded cause.");
            var assessment = workloadStart is { } ws ? AssessResources(tracker.Current, session, ws, workloadEnd ?? _clock.GetUtcNow()) : null;
            await tracker.FinishAsync((store, r) => FinishAsync(store, r,
                new PerformanceProviderResult { State = PerformanceRunState.Cancelled, Reason = "Stopped before the provider produced a result.", MetricsSource = r.ProviderId }, assessment));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError("Performance test {RunId} failed to execute: {Type}", run.RunId, ex.GetType().Name);
            try
            {
                await StopMonitorAsync();
                if (session is not null) await session.StopAsync();
                tracker.RequestStop(PerformanceStopReason.ProviderFailure, $"The provider failed ({ex.GetType().Name}).");
                var assessment = workloadStart is { } ws ? AssessResources(tracker.Current, session, ws, workloadEnd ?? _clock.GetUtcNow()) : null;
                await tracker.FinishAsync((store, r) => store.SaveRunAsync(r with { State = PerformanceRunState.ExecutionFailed,
                    StateReason = $"The performance test could not be executed ({ex.GetType().Name}).", FinishedAt = _clock.GetUtcNow(), Resources = assessment,
                    ThresholdResults = PerformanceTestRules.EvaluateThresholds(r.DefinitionSnapshot.Thresholds, null, false), ExecutionEvidence = Ended(r.ExecutionEvidence, "Execution failed") }, CancellationToken.None));
            }
            catch (Exception inner) when (inner is not OutOfMemoryException) { logger.LogError("Performance test {RunId} result could not be stored: {Type}", run.RunId, inner.GetType().Name); }
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            _tasks.TryRemove(queued.RunId, out _);
            if (_active.TryRemove(queued.RunId, out var cts)) cts.Dispose();
            _trackers.TryRemove(queued.RunId, out _);
            _activeByEnvironment.TryRemove(new KeyValuePair<string, Guid>(queued.EnvironmentId, queued.RunId));
        }
    }

    private PerformanceExecutionEvidence? Ended(PerformanceExecutionEvidence? e, string outcome)
    {
        if (e is null) return null;
        var now = _clock.GetUtcNow();
        return PerformanceLiveMonitor.Event(e with { Stage = PerformanceRunStage.Finished, StageChangedAt = now }, now, outcome);
    }

    /// <summary>
    /// The run's final execution evidence from the provider result: the final telemetry read (taken before the run directory was removed), exit
    /// code, result artifact, cleanup and log tail; a stop cause is inferred only from the result itself (timeout, unexpected exit), never guessed.
    /// </summary>
    internal PerformanceExecutionEvidence? FinalEvidence(PerformanceExecutionEvidence? e, PerformanceProviderResult result)
    {
        if (e is null) return null;
        var now = _clock.GetUtcNow();
        if (result.Telemetry is { } t)
        {
            e = e with { TelemetrySource = result.TelemetrySource ?? e.TelemetrySource };
            e = t.StreamOpened
                ? e with
                {
                    Telemetry = PerformanceTelemetryState.Collected, ScriptInitializedAt = e.ScriptInitializedAt ?? t.StreamOpenedAt, RequestsObserved = t.Requests, FailedRequestsObserved = t.FailedRequests,
                    IterationsObserved = t.Iterations, FirstRequestAt = t.FirstRequestAt, LastRequestAt = t.LastRequestAt, FirstIterationAt = t.FirstIterationAt, LastIterationAt = t.LastIterationAt,
                    LastHeartbeatAt = t.LastHeartbeatAt ?? e.LastHeartbeatAt,
                }
                : e with { Telemetry = t.Error is not null ? PerformanceTelemetryState.Failed : PerformanceTelemetryState.Unavailable };
        }
        else if (e.Telemetry == PerformanceTelemetryState.Receiving) e = e with { Telemetry = PerformanceTelemetryState.Unavailable };
        var reason = e.StopReason != PerformanceStopReason.None ? e.StopReason
            : result.State == PerformanceRunState.TimedOut ? PerformanceStopReason.ExecutionTimeout
            : result.State == PerformanceRunState.ExecutionFailed && result.ExitCode is { } code && code is not (0 or 99) ? PerformanceStopReason.UnexpectedProcessExit
            : result.State == PerformanceRunState.ExecutionFailed && e.Container == PerformanceObservedState.Gone && result.ExitCode is null ? PerformanceStopReason.ContainerStoppedExternally
            : PerformanceStopReason.None;
        if (reason != e.StopReason) e = PerformanceLiveMonitor.Event(e with { StopReason = reason }, now, PerformanceExecutionAssessment.StopReasonLabel(reason));
        if (result.ExitCode is { } exit) e = PerformanceLiveMonitor.Event(e with { ExitCode = exit, K6ExitedAt = e.K6ExitedAt ?? now, K6Process = PerformanceObservedState.Exited }, now, "k6 exited", $"exit code {exit}");
        e = e with
        {
            ResultArtifact = result.ResultArtifact, ResultArtifactDetail = result.ResultArtifactDetail, ContainerRemoved = result.ContainerRemoved,
            LogTail = result.Diagnostics is { Length: > 0 } log ? (log.Length > 4096 ? "…" + log[^4096..] : log) : e.LogTail, AppearsStuck = false,
        };
        if (result.ContainerRemoved is { } removed) e = PerformanceLiveMonitor.Event(e, now, "Container cleanup", removed ? "removed" : "not confirmed removed");
        return PerformanceLiveMonitor.Event(e with { Stage = PerformanceRunStage.Finished, StageChangedAt = now }, now, $"Finished · {result.State}");
    }

    /// <summary>Threshold evaluation (only a Completed run with complete metrics is assessed), shared-scoring quality, and drift against the
    /// baseline that was active when the run was created.</summary>
    internal async Task<PerformanceTestRun> FinishAsync(PerformanceTestStore store, PerformanceTestRun run, PerformanceProviderResult result, ResourceStabilityAssessment? resourceAssessment = null)
    {
        var assessable = result.State == PerformanceRunState.Completed && result.Metrics is not null && !result.MetricsPartial;
        var thresholds = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, result.Metrics, assessable);
        // Shared scoring: evaluable resource policies count with the thresholds; resource evidence gaps are coverage, never a failure.
        var (quality, verdict) = PerformanceTestRules.Quality(thresholds, assessable ? Resources.ResourceStabilityAnalyzer.QualityOutcomes(resourceAssessment).ToList() : []);
        var limitations = new List<string>(result.Limitations);
        if (run.DefinitionSnapshot.Thresholds.Count == 0 && assessable) limitations.Add("No thresholds were configured: metrics are measured, not assessed (no quality score).");
        limitations.Add("Load-test metrics describe this workload from this load generator; they are not single-request API latency (API Quality Review) or browser performance (Frontend Quality Review).");
        limitations.Add("Observability enrichment is not configured; the run window is recorded for later telemetry correlation. Correlation is not root cause.");
        PerformanceDriftAssessment? drift = null;
        var evidence = FinalEvidence(run.ExecutionEvidence, result);
        var finishedAt = _clock.GetUtcNow();
        // The stop cause leads the reason ("Cancelled by user after 1:35. …"); a cleanup outcome never becomes the run's outcome.
        var stateReason = evidence is { StopReason: not PerformanceStopReason.None } stopped && result.State is not PerformanceRunState.Completed
            ? $"{PerformanceExecutionAssessment.StopReasonLabel(stopped.StopReason)}{(run.StartedAt is { } began ? $" after {Elapsed(finishedAt - began)}" : "")}. {result.Reason}".Trim()
            : result.Reason;
        var finished = run with
        {
            ExecutionEvidence = evidence,
            State = result.State, StateReason = stateReason, FinishedAt = finishedAt, Metrics = result.Metrics, MetricsPartial = result.MetricsPartial,
            ThresholdResults = thresholds, Quality = quality, Verdict = verdict, ProviderVersion = result.ProviderVersion ?? run.ProviderVersion, MetricsSource = result.MetricsSource,
            RuntimeId = result.RuntimeId ?? run.RuntimeId, RuntimeVersion = result.RuntimeVersion ?? run.RuntimeVersion, ContainerImage = result.ContainerImage ?? run.ContainerImage,
            ImageDigest = result.ImageDigest ?? run.ImageDigest,
            ProviderDiagnostics = result.Diagnostics, Limitations = limitations, Resources = resourceAssessment,
        };
        ResourceDriftAssessment? resourceDrift = null;
        if (assessable && run.BaselineIdAtRun is { } baselineId && await store.BaselineAsync(baselineId) is { } baseline && await store.RunAsync(baseline.RunId) is { } baselineRun)
        {
            drift = PerformanceTestRules.Drift(finished, baselineRun, baseline, run.DefinitionSnapshot.DriftPolicies);
            resourceDrift = Resources.ResourceStabilityAnalyzer.Drift(finished, baselineRun, baseline.BaselineId, run.DefinitionSnapshot.ResourceObservation?.DriftPolicies ?? []);
        }
        return await store.SaveRunAsync(finished with { Drift = drift, ResourceDrift = resourceDrift }, CancellationToken.None);
    }

    private static string Elapsed(TimeSpan span) => span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
}
