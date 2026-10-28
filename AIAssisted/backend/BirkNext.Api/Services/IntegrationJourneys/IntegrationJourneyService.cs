using System.Collections.Concurrent;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys;

public sealed class IntegrationJourneyRequestException(string message) : Exception(message);

public interface IIntegrationJourneyService
{
    Task<IReadOnlyList<IntegrationJourneyPackView>> PacksAsync(string environmentId, CancellationToken ct);
    Task<ArchitectureRuleReport> RulesAsync(string environmentId, string packId, Guid? snapshotId, CancellationToken ct);
    /// <summary>Per configuration requirement of the pack: what its IQR-bound source declares (suggested, conflict, available but not bound).</summary>
    Task<TestPackSourceGuidance> SourceGuidanceAsync(string environmentId, string packId, CancellationToken ct) =>
        Task.FromResult(new TestPackSourceGuidance(packId, []));
    /// <summary>Stores the pack's safe, bound, unambiguous source-derived values into empty fields (explicit action; never over stored values).</summary>
    Task<SourceConfigurationApplyResult> ApplySourceConfigurationAsync(string environmentId, string packId, CancellationToken ct) =>
        Task.FromResult(new SourceConfigurationApplyResult([], []));
    IReadOnlyList<IntegrationJourneyCatalogEntry> Catalog();
    /// <summary>Every pack's logical sources and the stored snapshots they were matched against (read-only). A blank environment is allowed:
    /// bindings belong to a target environment, so without one nothing reads as bound.</summary>
    Task<SourceSetupView> SourceSetupAsync(string? environmentId, CancellationToken ct) => Task.FromResult(new SourceSetupView());
    Task<IReadOnlyList<IntegrationTemplateView>> IntegrationTemplatesAsync(string? environmentId, string? environmentType, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IntegrationTemplateView>>([]);
    /// <summary>What the packs know about each configured integration they recognize (reference metadata, logical source state, canonical
    /// binding, schema candidates) — read-only input for Integration Quality Review's capability profiles.</summary>
    Task<IReadOnlyList<IntegrationReviewEvidenceFacts>> ReviewFactsAsync(string environmentId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IntegrationReviewEvidenceFacts>>([]);
    /// <summary>Starts a run. The initiator is the authenticated caller's stable id, for per-caller execution state such as a sign-in; never persisted.</summary>
    Task<IntegrationJourneyRun> StartAsync(IntegrationJourneyRunRequest request, string? initiator, CancellationToken ct);
    /// <summary>Validates a run without executing it: no external call, no gate, no runner, no history. Every gate StartAsync applies is reported.</summary>
    Task<JourneyRunValidation> ValidateAsync(IntegrationJourneyRunRequest request, CancellationToken ct) => throw new NotSupportedException();
    Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<IntegrationJourneyRunSummary>> HistoryAsync(IntegrationJourneyHistoryQuery query, CancellationToken ct);
    /// <summary>A recorded run's Payload &amp; Mapping Flow from its stored evidence, or null when the run or its pack has none.</summary>
    Task<PayloadFlowView?> PayloadFlowAsync(Guid runId, CancellationToken ct) => Task.FromResult<PayloadFlowView?>(null);
}

/// <summary>One in-flight run per environment and journey.</summary>
public sealed class IntegrationJourneyRunGate
{
    private readonly ConcurrentDictionary<string, Guid> _leases = new(StringComparer.Ordinal);
    public bool TryAcquire(string key, Guid runId) => _leases.TryAdd(key, runId);
    public void Release(string key, Guid runId) => _leases.TryRemove(new KeyValuePair<string, Guid>(key, runId));
}

/// <summary>
/// Generic journey lifecycle: readiness (trusted environment, IQR integrations, step coverage, pack prerequisites; delegated journeys reuse the
/// Active Event readiness of their provider), explicit confirmation, one run per journey, ordered execution through the shared runner, and
/// durable history without unredacted payloads. Safety is enforced here and cannot be relaxed by a pack: Production and unknown environments, untrusted
/// environments, unenrolled integrations, uncovered steps and unsupported scenarios never run.
/// </summary>
public sealed class IntegrationJourneyService(
    IIntegrationJourneyPackRegistry packs,
    IIntegrationCatalogService catalog,
    ITrustedExecutionEnvironmentRegistry environments,
    IReviewSourceEvidenceProvider sources,
    IActiveEventLifecycleService activeEvents,
    IntegrationJourneyRunner runner,
    IntegrationJourneyRunStore history,
    IntegrationJourneyRunGate gate,
    TimeProvider clock,
    ILogger<IntegrationJourneyService> logger,
    BirkNext.Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore? bindings = null,
    BirkNext.Api.Services.TestData.TestDataCatalog? testData = null) : IIntegrationJourneyService
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    /// <summary>Prerequisite keys of the journey's declared IQR integration requirements.</summary>
    public const string IntegrationKeyPrefix = "integration:";

    public async Task<IReadOnlyList<IntegrationJourneyPackView>> PacksAsync(string environmentId, CancellationToken ct)
    {
        var context = await ContextAsync(environmentId, ct);
        var views = new List<IntegrationJourneyPackView>();
        foreach (var pack in packs.Packs)
        {
            var journeys = new List<IntegrationJourneyView>();
            var packContext = context with { ResolvedConfiguration = await ResolveConfigurationAsync(pack, context, ct) };
            foreach (var journey in pack.Journeys.OrderBy(item => item.Order))
                journeys.Add(await ViewAsync(pack, journey, packContext, ct));
            views.Add(new IntegrationJourneyPackView
            {
                PackId = pack.PackId, PackVersion = pack.PackVersion, DisplayName = pack.DisplayName, Description = pack.Description,
                EnvironmentId = environmentId, EnvironmentTrusted = TrustProblem(context) is null, EnvironmentDetail = TrustProblem(context) ?? context.TrustDetail,
                Journeys = journeys, Limitations = pack.Limitations, EvidenceLimitations = pack.EvidenceLimitations(journeys),
            });
        }
        return views;
    }

    public async Task<ArchitectureRuleReport> RulesAsync(string environmentId, string packId, Guid? snapshotId, CancellationToken ct)
    {
        var pack = packs.Find(packId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        if (!sources.SourceAnalysisEnabled)
            return ArchitectureRuleEvaluator.Evaluate(pack.PackId, pack.Rules, null) with { Limitations = [ReviewSourceEvidenceProvider.Disabled] };
        // Rules are assessed only against the canonical evidence: a snapshot Integration Quality Review binds to one of this pack's
        // integrations. Without a binding every rule is Not assessed; the newest stored snapshot is named as a non-authoritative candidate
        // and never evaluated. An explicitly requested snapshot is assessed only when it is one of the bound snapshots.
        var context = await ContextAsync(environmentId, ct);
        var boundSnapshots = BoundSnapshots(pack, context);
        if (snapshotId is { } id)
        {
            var requested = await sources.ResolveAsync(environmentId, id, ct) ?? throw new IntegrationJourneyRequestException("The selected Source Analysis snapshot was not found.");
            return boundSnapshots.Any(item => item.Id == id) ? ArchitectureRuleEvaluator.Evaluate(pack.PackId, pack.Rules, requested)
                : ArchitectureRuleEvaluator.NotBound(pack.PackId, pack.Rules, requested,
                    $"Not assessed: snapshot {requested.Archive.FileName} is not the source evidence bound in Integration Quality Review → Evidence for this pack.");
        }
        if (pack.Rules.Any(rule => rule.SourceRole is not null)) return RulesBySourceRole(pack, context, boundSnapshots);
        var bound = boundSnapshots.FirstOrDefault(snapshot => snapshot.Architecture is not null) ?? boundSnapshots.FirstOrDefault();
        return bound is not null ? ArchitectureRuleEvaluator.Evaluate(pack.PackId, pack.Rules, bound)
            : ArchitectureRuleEvaluator.NotBound(pack.PackId, pack.Rules, context.SourceSnapshots.FirstOrDefault(item => item.Architecture is not null));
    }

    /// <summary>
    /// Each rule is assessed against the canonical binding of the logical source it needs (its source role), never against whichever snapshot
    /// happens to be bound: a rule about a component of another source is "Not assessed — needs that source", not "component not found".
    /// An available but unbound snapshot of the needed source is named, never used.
    /// </summary>
    private static ArchitectureRuleReport RulesBySourceRole(IIntegrationJourneyPack pack, JourneyPackContext context, IReadOnlyList<IqrSourceSnapshot> packBound)
    {
        var statuses = TestPackSources.Statuses(pack, context);
        var findings = new Dictionary<string, ArchitectureRuleFinding>(StringComparer.Ordinal);
        var sources = new List<ArchitectureRuleSource>();
        var limitations = new List<string> { "Each rule is assessed against the bound source of the logical source it needs." };
        foreach (var group in pack.Rules.GroupBy(rule => rule.SourceRole))
        {
            var rules = group.ToArray();
            var role = TestPackSources.Role(pack, group.Key);
            var candidates = role is null ? packBound : TestPackSources.Authoritative(pack, context, role);
            var snapshot = candidates.FirstOrDefault(item => item.Architecture is not null) ?? candidates.FirstOrDefault();
            var status = role is null ? null : statuses.FirstOrDefault(item => item.Role.Key == role.Key);
            if (snapshot is null)
            {
                var others = statuses.Where(item => item.State == TestPackSourceState.Bound && item.Role.Key != role?.Key).Select(item => item.Role.Label).ToArray();
                var reason = role is null ? ArchitectureRuleEvaluator.NotBoundReason
                    : $"Not assessed — this rule requires {role.Label} source evidence."
                      + (others.Length > 0 ? $" The currently bound source is {string.Join(", ", others)}." : " No source is bound for it.")
                      + (status is { State: TestPackSourceState.AvailableNotBound } ? $" {string.Join(", ", status.AvailableArchives)} is available but not bound." : "")
                      + (status is { State: TestPackSourceState.Missing } ? $" No {role.Label} source is imported." : "");
                foreach (var finding in ArchitectureRuleEvaluator.NotBound(pack.PackId, rules, null, reason).Findings) findings[finding.RuleId] = finding;
                sources.AddRange(rules.Select(rule => new ArchitectureRuleSource(rule.RuleId, role?.Key, role?.Label, null, status?.AvailableArchives.FirstOrDefault(),
                    status?.State ?? TestPackSourceState.Missing)));
                continue;
            }
            var report = ArchitectureRuleEvaluator.Evaluate(pack.PackId, rules, snapshot);
            foreach (var finding in report.Findings) findings[finding.RuleId] = finding;
            limitations.AddRange(report.Limitations);
            sources.AddRange(rules.Select(rule => new ArchitectureRuleSource(rule.RuleId, role?.Key, role?.Label, snapshot.Archive.FileName, null, TestPackSourceState.Bound)));
        }
        var archives = sources.Select(item => item.BoundArchive).OfType<string>().Distinct().ToArray();
        var single = archives.Length == 1 ? context.SourceSnapshots.FirstOrDefault(item => item.Archive.FileName == archives[0]) : null;
        return new ArchitectureRuleReport(pack.PackId, single?.Id, archives.Length == 1 ? archives[0] : null,
            [.. pack.Rules.Where(rule => findings.ContainsKey(rule.RuleId)).Select(rule => findings[rule.RuleId])], [.. limitations.Distinct()]) { RuleSources = sources };
    }

    public async Task<TestPackSourceGuidance> SourceGuidanceAsync(string environmentId, string packId, CancellationToken ct)
    {
        var pack = packs.Find(packId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        if (pack.RequiredConfiguration.Count == 0) return new TestPackSourceGuidance(pack.PackId, []);
        if (!sources.SourceAnalysisEnabled)
            return new TestPackSourceGuidance(pack.PackId, []) { Limitations = [ReviewSourceEvidenceProvider.Disabled] };
        var context = await ContextAsync(environmentId, ct);
        return new TestPackSourceGuidance(pack.PackId, await ResolveConfigurationAsync(pack, context, ct))
        {
            Sources = TestPackSources.Statuses(pack, context),
            Limitations = ["Only the Source Analysis snapshot bound in Integration Quality Review → Evidence can suggest a value; a suggestion is never applied automatically.",
                "Declared in source is not runtime verified."],
        };
    }

    /// <summary>
    /// The pack's configuration values, resolved once by the shared precedence for every consumer (Integration Quality Review, the test pack's
    /// pages and prerequisites): the value its consumer stored, else the configured integration record that owns it, else the canonical bound
    /// source, else the Test Pack reference, else the pack's safe default. Lower tiers that disagree stay visible. Reading stores nothing.
    /// </summary>
    internal async Task<IReadOnlyList<SourceConfigurationSuggestion>> ResolveConfigurationAsync(IIntegrationJourneyPack pack, JourneyPackContext context, CancellationToken ct)
    {
        if (pack.RequiredConfiguration.Count == 0) return [];
        var configured = await pack.ConfiguredValuesAsync(context, ct);
        var packBound = BoundSnapshots(pack, context);
        IReadOnlyList<SourceConfigurationSuggestion> suggestions = pack.RequiredConfiguration.Select(requirement =>
        {
            var roles = requirement.SourceRoles.Select(key => TestPackSources.Role(pack, key)).OfType<TestPackSourceRole>().ToArray();
            if (roles.Length == 0) return SourceAnalysis.SourceConfigurationGuidance.Suggest([requirement], packBound, context.SourceSnapshots, configured)[0];
            // Only the authoritative snapshots of the requirement's own sources count; the pool for "available but not bound" is those
            // sources' recognized snapshots — never any other snapshot.
            var bound = roles.SelectMany(role => TestPackSources.Authoritative(pack, context, role)).DistinctBy(item => item.Id).ToArray();
            var available = roles.SelectMany(role => TestPackSources.Available(pack, context, role)).DistinctBy(item => item.Id).ToArray();
            var labels = string.Join(" or ", roles.Select(role => role.Label));
            var suggestion = SourceAnalysis.SourceConfigurationGuidance.Suggest([requirement], bound, [.. bound, .. available], configured)[0] with { SourceRoleLabel = labels };
            // Precedence: bound imported evidence, then the pack's Test Pack reference — a difference is shown, never resolved silently.
            var referenced = TestPackReferences.Values(pack, roles, requirement.SemanticKey, requirement.ScopeHint);
            var reference = referenced.Count == 0 ? null : string.Join(", ", referenced.Select(item => item.Value).Distinct(StringComparer.Ordinal));
            if (reference is not null && suggestion.State is SourceSuggestionState.Suggested or SourceSuggestionState.Conflict)
                return suggestion with
                {
                    ReferenceValue = reference,
                    Detail = string.Equals(suggestion.Value, reference, StringComparison.OrdinalIgnoreCase) ? suggestion.Detail
                        : $"{suggestion.Detail} The Test Pack reference declares {reference}; the bound imported source stays authoritative.",
                };
            if (reference is not null)
                return suggestion with
                {
                    State = SourceSuggestionState.Reference, Value = reference, ReferenceValue = reference, Candidates = [new SourceConfigurationCandidate(reference, referenced)],
                    Detail = "From the Test Pack reference (built into BirkNext). Reference metadata — not imported source evidence and not runtime verified."
                        + (suggestion.UnboundSources.Count > 0 ? $" {string.Join(", ", suggestion.UnboundSources)} also declares it but is not bound in Integration Quality Review; its value is not used." : ""),
                };
            if (suggestion.State != SourceSuggestionState.NotDeclared) return suggestion;
            return bound.Length > 0
                ? suggestion with { Detail = $"The bound {labels} source does not declare it. Expected evidence: {requirement.EvidenceType}." }
                : available.Length > 0
                    ? suggestion with { State = SourceSuggestionState.AvailableNotBound, UnboundSources = [.. available.Select(item => item.Archive.FileName).Distinct()],
                        Detail = $"Needs {labels} source evidence: {string.Join(", ", available.Select(item => item.Archive.FileName).Distinct())} is available but not bound." }
                    : suggestion with { State = SourceSuggestionState.SourceMissing, Detail = $"{labels} source is missing. Expected evidence: {requirement.EvidenceType}." };
        }).Select(suggestion => WithIntegrationAndDefault(pack, context, suggestion)).ToArray();
        return suggestions;
    }

    /// <summary>Adds the tiers that are not source evidence — the configured integration record that owns the value, and the pack's safe
    /// default — and resolves the value by the shared precedence.</summary>
    private static SourceConfigurationSuggestion WithIntegrationAndDefault(IIntegrationJourneyPack pack, JourneyPackContext context, SourceConfigurationSuggestion suggestion)
    {
        var requirement = suggestion.Requirement;
        var owners = Array.Empty<(IntegrationDefinition Record, string Value)>();
        if (requirement.IntegrationRequirementKey is { } key && requirement.IntegrationField is { } field
            && pack.Journeys.SelectMany(journey => journey.Integrations).FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal)) is { } journeyRequirement)
            owners = [.. context.Catalog.Integrations.Where(integration => pack.Matches(journeyRequirement, integration))
                .OrderByDescending(integration => integration.Enabled).ThenBy(integration => integration.Id, StringComparer.Ordinal)
                .Select(integration => (Record: integration, Value: IntegrationFieldValue(integration, field)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value)).Select(item => (item.Record, item.Value!.Trim()))];
        var resolved = SourceConfigurationPrecedence.Resolve(suggestion with
        {
            IntegrationValue = owners.Length > 0 ? owners[0].Value : null, IntegrationName = owners.Length > 0 ? owners[0].Record.DisplayName : null,
            DefaultValue = requirement.DefaultValue,
        });
        // Two configured records of the same integration that disagree: the first (enabled) one is used, the other is shown, never merged.
        var others = owners.Skip(1).Where(item => !SourceConfigurationPrecedence.Agrees(owners[0].Value, item.Value, false))
            .Select(item => new ConfigurationAlternative(ConfigurationValueOrigin.ConfiguredIntegration, item.Value));
        return resolved with { Disagreements = [.. resolved.Disagreements, .. others] };
    }

    /// <summary>The value of one integration record field, by its declared name (never anything sensitive: records hold references only).</summary>
    internal static string? IntegrationFieldValue(IntegrationDefinition integration, string field) => field switch
    {
        nameof(IntegrationDefinition.DisplayName) => integration.DisplayName,
        nameof(IntegrationDefinition.Kind) => integration.Kind.ToString(),
        nameof(IntegrationDefinition.SystemName) => integration.SystemName,
        nameof(IntegrationDefinition.Producer) => integration.Producer,
        "Consumer" => integration.Consumer.DisplayName,
        nameof(IntegrationDefinition.EndpointOrTopic) => integration.EndpointOrTopic,
        nameof(IntegrationDefinition.SourceResource) => integration.SourceResource,
        nameof(IntegrationDefinition.ConsumerGroup) => integration.ConsumerGroup,
        _ => null,
    };

    public async Task<SourceSetupView> SourceSetupAsync(string? environmentId, CancellationToken ct)
    {
        var environment = environmentId?.Trim() ?? "";
        if (!sources.SourceAnalysisEnabled)
            return new SourceSetupView { EnvironmentId = environment, Limitations = [ReviewSourceEvidenceProvider.Disabled] };
        JourneyPackContext context;
        if (environment.Length == 0)
            context = new JourneyPackContext("", null, "No target environment is selected.", new IntegrationCatalog(), await sources.ListAsync("", ct));
        else
            context = await ContextAsync(environment, ct);
        var names = context.Catalog.Integrations.ToDictionary(item => item.Id, item => item.DisplayName, StringComparer.Ordinal);
        var snapshots = context.SourceSnapshots.Select(snapshot => new SourceSetupSnapshot(snapshot.Id, snapshot.Archive.FileName, snapshot.Archive.Sha256, snapshot.AnalyzedAt,
            snapshot.Status.ToString())
        {
            ImportId = snapshot.ProjectImport?.ImportId,
            Repository = snapshot.Repository?.DisplayName,
            BoundIntegrations = [.. context.Bindings.Where(binding => binding.SnapshotId == snapshot.Id)
                .Select(binding => names.TryGetValue(binding.IntegrationId, out var name) ? name : binding.IntegrationId).Order(StringComparer.Ordinal)],
            InfrastructureFormats = TestPackSources.InfrastructureFormats(snapshot),
            InfrastructureAutomation = TestPackSources.InfrastructureAutomation(snapshot),
            ConfigurationEnvironments = BirkNext.SourceDomains.SourceEnvironmentValueExtractor.Environments(snapshot),
            Areas = SourceSetupAreas.Of(snapshot),
        }).ToArray();
        return new SourceSetupView
        {
            EnvironmentId = environment, BindingsRead = environment.Length > 0, Snapshots = snapshots,
            Packs = [.. packs.Packs.Where(pack => pack.SourceRoles.Count > 0).Select(pack => new TestPackSourceSetup(pack.PackId, pack.DisplayName, TestPackSources.Statuses(pack, context))
            {
                JourneyNames = pack.Journeys.GroupBy(journey => journey.JourneyId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal),
            })],
            Limitations = environment.Length > 0 ? ["Bound means the canonical Integration Quality Review evidence binding of this target environment; a newer snapshot never replaces it."]
                : ["No target environment is selected: IQR evidence bindings belong to a target environment, so no source reads as bound."],
        };
    }

    /// <summary>Add-integration templates of every pack, resolved for the environment. The environment type comes from the trusted environment
    /// record when there is one, else from the caller's profile; production never receives a template endpoint.</summary>
    public async Task<IReadOnlyList<IntegrationTemplateView>> IntegrationTemplatesAsync(string? environmentId, string? environmentType, CancellationToken ct)
    {
        var environment = environmentId?.Trim() ?? "";
        var context = environment.Length == 0
            ? new JourneyPackContext("", null, "No target environment is selected.", new IntegrationCatalog(), sources.SourceAnalysisEnabled ? await sources.ListAsync("", ct) : [])
            : await ContextAsync(environment, ct);
        var type = context.TrustedEnvironment?.EnvironmentType ?? environmentType;
        return [.. packs.Packs.SelectMany(pack => pack.IntegrationTemplates.Select(template => IntegrationTemplates.Resolve(pack, context, template, type)))];
    }

    public async Task<IReadOnlyList<IntegrationReviewEvidenceFacts>> ReviewFactsAsync(string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return [];
        var context = await ContextAsync(environmentId.Trim(), ct);
        var facts = new List<IntegrationReviewEvidenceFacts>();
        foreach (var pack in packs.Packs)
            foreach (var template in pack.IntegrationTemplates.Where(t => t.RequirementKey is not null))
            {
                if (pack.Journeys.SelectMany(journey => journey.Integrations).FirstOrDefault(item => item.Key == template.RequirementKey) is not { } requirement) continue;
                var view = IntegrationTemplates.Resolve(pack, context, template, context.TrustedEnvironment?.EnvironmentType);
                foreach (var integration in context.Catalog.Integrations.Where(item => item.Enabled && pack.Matches(requirement, item)))
                {
                    // The integration's OWN canonical binding decides Bound; a match elsewhere is availability only.
                    var bound = context.BoundSnapshot(integration.Id);
                    var evidence = view.EvidenceSource;
                    facts.Add(new IntegrationReviewEvidenceFacts(integration.Id)
                    {
                        PackName = pack.DisplayName, ReferenceTitle = view.ReferenceSource?.Reference?.Title,
                        AuthenticationReference = view.AuthenticationReference, ContractReference = view.ContractReference,
                        SourceRole = evidence?.Role.Label, SourceMatches = evidence?.Candidates.Count ?? 0, BoundArchive = bound?.Archive.FileName,
                        SourceState = bound is not null ? TestPackSourceState.Bound
                            : evidence is null ? null : evidence.Candidates.Count == 0 ? evidence.State
                            : evidence.Candidates.Select(c => c.Fingerprint).Distinct(StringComparer.Ordinal).Count() > 1 ? TestPackSourceState.ReviewRequired : TestPackSourceState.AvailableNotBound,
                        SchemaName = view.SchemaName,
                        SchemaFiles = [.. view.SchemaCandidates.Where(c => bound is null || c.SnapshotId == bound.Id).Select(c => c.File).Distinct(StringComparer.Ordinal)],
                        SourceSide = template.EvidenceSourceSide, ReferenceSide = template.Reference?.Side ?? IntegrationSourceSide.Unknown,
                    });
                }
            }
        return [.. facts.DistinctBy(item => item.IntegrationId)];
    }

    public async Task<SourceConfigurationApplyResult> ApplySourceConfigurationAsync(string environmentId, string packId, CancellationToken ct)
    {
        var pack = packs.Find(packId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        var guidance = await SourceGuidanceAsync(environmentId, packId, ct);
        var eligible = new Dictionary<string, SourceConfigurationSuggestion>(StringComparer.Ordinal);
        var notApplied = new List<SourceConfigurationNotApplied>();
        foreach (var suggestion in guidance.Suggestions.Where(item => item.Requirement.AutoApply))
        {
            // Only values the shared precedence resolved from evidence, the reference or the pack default are stored, into EMPTY fields; a value
            // a configured integration owns is read from that record and never copied (two copies would drift).
            var reason = suggestion switch
            {
                { State: SourceSuggestionState.Conflict } => "Review required: the bound source declares different values.",
                { ConfiguredValue: not null } => "A value is already configured; it is kept.",
                { ReviewRequired: true } => "Review required: the sources of this value disagree.",
                { ResolvedFrom: ConfigurationValueOrigin.ConfiguredIntegration } => $"Read from the configured integration {suggestion.IntegrationName}; it is not copied.",
                { ResolvedFrom: ConfigurationValueOrigin.None, State: SourceSuggestionState.AvailableNotBound } => "Available but not bound: bind the snapshot in Integration Quality Review → Evidence first.",
                { ResolvedFrom: ConfigurationValueOrigin.None, State: SourceSuggestionState.NotDeclared } => "Not declared in the bound source.",
                { ResolvedValue: null } => "No safe value is known.",
                _ when Integrations.MessageFlowCredentialGuard.Reason(suggestion.ResolvedValue!) is not null => "The value is not a safe reference.",
                _ => null,
            };
            if (reason is null) eligible[suggestion.Requirement.Key] = suggestion;
            else notApplied.Add(new(suggestion.Requirement.Key, suggestion.Requirement.Label, reason));
        }
        if (eligible.Count == 0) return new SourceConfigurationApplyResult([], notApplied);
        var stored = await pack.ApplyConfigurationAsync(await ContextAsync(environmentId, ct), eligible.ToDictionary(item => item.Key, item => item.Value.ResolvedValue!), ct);
        foreach (var key in eligible.Keys.Except(stored))
            notApplied.Add(new(key, eligible[key].Requirement.Label, "Not stored: the field is not empty or the consumer does not accept this value."));
        return new SourceConfigurationApplyResult(
            [.. stored.Where(eligible.ContainsKey).Select(key => new SourceConfigurationApplied(key, eligible[key].Requirement.Label, eligible[key].ResolvedValue!,
                eligible[key].ResolvedFrom == ConfigurationValueOrigin.CanonicalSource
                    ? string.Join("; ", eligible[key].Candidates.SelectMany(candidate => candidate.Provenance).Select(SourceAnalysis.SourceConfigurationGuidance.Provenance).Distinct())
                    : SourceConfigurationPrecedence.Label(eligible[key].ResolvedFrom)))],
            notApplied);
    }

    /// <summary>The journey's declared Test Data roles resolved through the shared catalog (aliases and states only; nothing is stored).</summary>
    private (IReadOnlyList<BirkNext.TestData.TestDataRequirement>, BirkNext.TestData.TestDataResolutionResult?, string?) ResolveTestData(IIntegrationJourneyPack pack, IntegrationJourneyDefinition journey)
    {
        var requirements = pack.TestDataRequirements(journey);
        if (requirements.Count == 0) return (requirements, null, null);
        if (testData is null) return (requirements, null, "The shared Test Data catalog is not available in this host.");
        try { return (requirements, testData.Resolve(requirements), null); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Test Data could not be resolved for journey {Journey}.", journey.JourneyId);
            return (requirements, null, "The Test Data catalog could not be read.");
        }
    }

    /// <summary>The source snapshots and the Test Data versions a run starts with: only deterministic resolutions (exactly one approved
    /// asset) are recorded, so the record never claims a version the run did not use.</summary>
    private JourneyRunEvidenceContext EvidenceContext(IIntegrationJourneyPack pack, IntegrationJourneyDefinition journey, JourneyPackContext context)
    {
        var (_, resolution, _) = ResolveTestData(pack, journey);
        var resolved = resolution?.Roles.Where(role => role.State == BirkNext.TestData.TestDataResolutionState.Resolved && role.AssetAlias is not null).Select(role => role.AssetAlias!).ToHashSet(StringComparer.Ordinal) ?? [];
        var assets = resolved.Count == 0 || testData is null ? [] : testData.View().Assets.Where(asset => resolved.Contains(asset.Alias)).Select(BirkNext.TestData.TestDataLifecycle.Usage).ToArray();
        return new JourneyRunEvidenceContext
        {
            PackVersion = pack.PackVersion,
            SourceSnapshots = [.. JourneyBoundSnapshots(pack, journey, context).Select(snapshot => new JourneyRunSourceReference(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt))],
            TestData = assets,
        };
    }

    /// <summary>The IQR-bound snapshots of the integrations that fulfil one journey's requirements (falls back to the pack's bound sources when
    /// the journey declares no integration).</summary>
    internal static IReadOnlyList<IqrSourceSnapshot> JourneyBoundSnapshots(IIntegrationJourneyPack pack, IntegrationJourneyDefinition journey, JourneyPackContext context) =>
        journey.Integrations.Count == 0 ? BoundSnapshots(pack, context)
            : journey.Integrations.SelectMany(requirement => context.Catalog.Integrations.Where(integration => pack.Matches(requirement, integration)))
                .Select(integration => context.BoundSnapshot(integration.Id)).OfType<IqrSourceSnapshot>().DistinctBy(snapshot => snapshot.Id).ToArray();

    /// <summary>The IQR-bound snapshots of the configured integrations that fulfil this pack's requirements, in requirement order.</summary>
    internal static IReadOnlyList<IqrSourceSnapshot> BoundSnapshots(IIntegrationJourneyPack pack, JourneyPackContext context) =>
        pack.Journeys.SelectMany(journey => journey.Integrations)
            .SelectMany(requirement => context.Catalog.Integrations.Where(integration => pack.Matches(requirement, integration)))
            .Select(integration => context.BoundSnapshot(integration.Id))
            .OfType<IqrSourceSnapshot>()
            .DistinctBy(snapshot => snapshot.Id)
            .ToArray();

    private async Task<IReadOnlyList<IntegrationEvidenceBinding>> BindingsAsync(string environmentId, CancellationToken ct)
    {
        if (bindings is null) return [];
        try { return await bindings.ListAsync(environmentId, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException or System.Data.Common.DbException)
        {
            logger.LogWarning(ex, "Integration evidence bindings could not be read for {Environment}.", environmentId);
            return [];
        }
    }

    public IReadOnlyList<IntegrationJourneyCatalogEntry> Catalog() => packs.Packs.SelectMany(pack => pack.Journeys.OrderBy(journey => journey.Order).Select(journey =>
        new IntegrationJourneyCatalogEntry(pack.PackId, journey.JourneyId, journey.DisplayName, journey.Steps
            .Select(step => new JourneyEdge(step.StepId, step.Label, step.Kind, step.FromComponent ?? step.Owner, step.ToComponent ?? step.Owner, step.Owner)).ToArray()))).ToArray();

    private const int MaxInputs = 20;
    private const int MaxInputLength = 200;

    public async Task<IntegrationJourneyRun> StartAsync(IntegrationJourneyRunRequest request, string? initiator, CancellationToken ct)
    {
        var pack = packs.Find(request.PackId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        var journey = pack.Journeys.FirstOrDefault(item => item.JourneyId == request.JourneyId) ?? throw new IntegrationJourneyRequestException("Unknown journey.");
        var scenario = journey.Scenarios.FirstOrDefault(item => item.ScenarioId == request.ScenarioId) ?? throw new IntegrationJourneyRequestException("Unknown scenario.");
        if (journey.ExecutionMode == JourneyExecutionMode.DelegatedToActiveEvent)
            throw new IntegrationJourneyRequestException("This journey is executed by Active Event Testing; start it from that panel.");
        if (!request.Confirmed) throw new IntegrationJourneyRequestException("Confirm the journey run before execution.");
        var inputs = PrepareInputs(pack, journey, scenario, request.Inputs ?? new Dictionary<string, string>());

        var context = await ContextAsync(request.EnvironmentId, ct);
        var view = await ViewAsync(pack, journey, context, ct);
        var started = clock.GetUtcNow();
        var run = new IntegrationJourneyRun
        {
            RunId = Guid.NewGuid(), PackId = pack.PackId, JourneyId = journey.JourneyId, JourneyName = journey.DisplayName,
            ScenarioId = scenario.ScenarioId, ScenarioName = scenario.DisplayName, EnvironmentId = request.EnvironmentId,
            EnvironmentName = context.TrustedEnvironment?.DisplayName ?? request.EnvironmentId, StartedAt = started,
            Steps = journey.Steps.Select(step => new JourneyStepResult { StepId = step.StepId, Label = step.Label, State = JourneyStepState.NotAssessed, Reason = "Not started." }).ToArray(),
            Limitations = journey.Limitations, InputSummary = inputs.Summary, InitiatedBy = SafeInitiator(initiator),
            EvidenceContext = EvidenceContext(pack, journey, context),
        };

        var blockers = new List<string>();
        if (scenario.Support != JourneyScenarioSupport.Supported) blockers.Add(scenario.NotAssessedBecause ?? "The scenario is not supported.");
        if (!view.RunnableScenarios.Contains(scenario.ScenarioId, StringComparer.Ordinal))
            blockers.AddRange(view.Prerequisites.Where(item => item.Mandatory && item.AppliesTo(scenario.ScenarioId) && item.State != JourneyReadinessState.Ready)
                .Select(item => $"{item.Label}: {item.Detail}"));
        if (journey.ExecutionMode != JourneyExecutionMode.JourneyRunner) blockers.Add("This journey is not executed by the journey runner.");
        if (TrustProblem(context) is { } trust) blockers.Add(trust);
        if (blockers.Count > 0) return await BlockedAsync(run, blockers.Distinct().ToArray(), ct);

        var key = $"{request.EnvironmentId}|{pack.PackId}|{journey.JourneyId}";
        if (!gate.TryAcquire(key, run.RunId)) return await BlockedAsync(run, ["Another run of this journey is in flight for this environment."], ct);
        try
        {
            if (!await history.InsertAsync(run, ct))
                return run with { OverallState = JourneyRunState.Blocked, CompletedAt = clock.GetUtcNow(), StateReason = "The run could not be recorded before execution; nothing was executed." };
            var integrations = Matches(pack, journey, context.Catalog).Where(item => item.Integration is { Enabled: true })
                .GroupBy(item => item.Requirement.Key).ToDictionary(group => group.Key, group => group.First().Integration!, StringComparer.Ordinal);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(RunTimeout);
            IntegrationJourneyRun completed;
            try
            {
                completed = await runner.ExecuteAsync(run, context.TrustedEnvironment!, journey, scenario, integrations, bounded.Token,
                    async (progress, token) => await history.UpdateAsync(progress, token), inputs.Values, initiator);
                // The pack may attach its derived record; it never alters the step evidence or the state the runner decided.
                completed = pack.CompleteRun(journey, scenario, completed) with { Steps = completed.Steps, OverallState = completed.OverallState, StateReason = completed.StateReason };
            }
            catch (OperationCanceledException)
            {
                completed = run with { OverallState = JourneyRunState.Cancelled, CompletedAt = clock.GetUtcNow(), StateReason = "The run was cancelled or timed out; remaining steps were not executed and nothing was retried." };
            }
            catch (Exception ex)
            {
                logger.LogError("Integration journey run {RunId} stopped unexpectedly ({ExceptionType}).", run.RunId, ex.GetType().Name);
                completed = run with { OverallState = JourneyRunState.NotVerified, CompletedAt = clock.GetUtcNow(), StateReason = "Execution stopped unexpectedly; no step result is asserted beyond what was recorded." };
            }
            await history.UpdateAsync(completed, CancellationToken.None);
            return completed;
        }
        finally { gate.Release(key, run.RunId); }
    }

    public async Task<JourneyRunValidation> ValidateAsync(IntegrationJourneyRunRequest request, CancellationToken ct)
    {
        var pack = packs.Find(request.PackId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        var journey = pack.Journeys.FirstOrDefault(item => item.JourneyId == request.JourneyId) ?? throw new IntegrationJourneyRequestException("Unknown journey.");
        var scenario = journey.Scenarios.FirstOrDefault(item => item.ScenarioId == request.ScenarioId) ?? throw new IntegrationJourneyRequestException("Unknown scenario.");
        var result = new JourneyRunValidation { PackId = pack.PackId, JourneyId = journey.JourneyId, ScenarioId = scenario.ScenarioId, EnvironmentId = request.EnvironmentId };
        var checks = new List<JourneyValidationCheck>
        {
            new("scenario", "Scenario supported", scenario.Support == JourneyScenarioSupport.Supported, scenario.Support == JourneyScenarioSupport.Supported ? scenario.DisplayName : scenario.NotAssessedBecause ?? "The scenario is not supported."),
            new("execution-mode", "Executed by the journey runner", journey.ExecutionMode == JourneyExecutionMode.JourneyRunner,
                journey.ExecutionMode == JourneyExecutionMode.JourneyRunner ? "Yes." : "This journey is executed elsewhere (Active Event Testing)."),
        };
        JourneyInputPreparation inputs;
        try { inputs = PrepareInputs(pack, journey, scenario, request.Inputs ?? new Dictionary<string, string>()); }
        catch (IntegrationJourneyRequestException ex)
        {
            checks.Add(new("inputs", "Scenario input", false, ex.Message));
            return result with { Valid = false, Checks = checks };
        }
        checks.Add(new("inputs", "Scenario input", true, "Valid typed inputs (validated by the backend; values are not kept)."));

        var context = await ContextAsync(request.EnvironmentId, ct);
        if (TrustProblem(context) is { } trust) checks.Add(new("trusted-environment", "Trusted non-production environment", false, trust));
        var view = await ViewAsync(pack, journey, context, ct);
        checks.AddRange(view.Prerequisites.Where(item => item.Mandatory && item.AppliesTo(scenario.ScenarioId))
            .Select(item => new JourneyValidationCheck($"prerequisite:{item.Key}", item.Label, item.State == JourneyReadinessState.Ready, item.Detail)));
        var packChecks = journey.ExecutionMode == JourneyExecutionMode.JourneyRunner && scenario.Support == JourneyScenarioSupport.Supported
            ? await pack.ValidateRunAsync(journey, scenario, inputs.Values, context, ct) : JourneyPackValidation.None;
        checks.AddRange(packChecks.Checks);
        var flow = journey.ExecutionMode == JourneyExecutionMode.JourneyRunner && scenario.Support == JourneyScenarioSupport.Supported
            ? await pack.PayloadFlowAsync(journey, scenario, new PayloadFlowRequest(inputs.Values, null), context, ct) : null;
        return result with { Valid = checks.All(item => item.Passed || !item.Blocking), Checks = checks, Preview = packChecks.Preview, InputSummary = inputs.Summary, PayloadFlow = flow };
    }

    public async Task<PayloadFlowView?> PayloadFlowAsync(Guid runId, CancellationToken ct)
    {
        if (await history.GetAsync(runId, ct) is not { } run || packs.Find(run.PackId) is not { } pack) return null;
        var journey = pack.Journeys.FirstOrDefault(item => item.JourneyId == run.JourneyId);
        var scenario = journey?.Scenarios.FirstOrDefault(item => item.ScenarioId == run.ScenarioId);
        if (journey is null || scenario is null) return null;
        return await pack.PayloadFlowAsync(journey, scenario, new PayloadFlowRequest(null, run), await ContextAsync(run.EnvironmentId, ct), ct);
    }

    /// <summary>History keeps the initiator only in its derived, safe form (local-session:… or authenticated-user:&lt;hash&gt;); anything else — a raw
    /// subject, e-mail or name — is not persisted.</summary>
    internal static string? SafeInitiator(string? initiator) =>
        initiator is { Length: <= 80 } value && System.Text.RegularExpressions.Regex.IsMatch(value, "^(local-session|authenticated-user):[a-z0-9-]{1,64}$") ? value : null;

    /// <summary>Generic checks (declared keys only, bounded size, required present), then the pack's typed validation. Nothing here is stored.</summary>
    private static JourneyInputPreparation PrepareInputs(IIntegrationJourneyPack pack, IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario,
        IReadOnlyDictionary<string, string> inputs)
    {
        var declared = scenario.Inputs ?? [];
        if (inputs.Count > MaxInputs || inputs.Any(item => item.Key.Length > 64 || (item.Value?.Length ?? 0) > MaxInputLength))
            throw new IntegrationJourneyRequestException("The run inputs exceed the supported size.");
        if (inputs.Keys.FirstOrDefault(key => declared.All(item => item.Key != key)) is { } unknown)
            throw new IntegrationJourneyRequestException($"The scenario does not declare the input \"{unknown}\".");
        if (declared.FirstOrDefault(item => item.Required && string.IsNullOrWhiteSpace(inputs.GetValueOrDefault(item.Key))) is { } missing)
            throw new IntegrationJourneyRequestException($"{missing.Label} is required.");
        var prepared = pack.PrepareInputs(journey, scenario, inputs);
        if (!prepared.Valid) throw new IntegrationJourneyRequestException(string.Join(" ", prepared.Errors));
        return prepared;
    }

    public Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct) => history.GetAsync(runId, ct);

    public async Task<IReadOnlyList<IntegrationJourneyRunSummary>> HistoryAsync(IntegrationJourneyHistoryQuery query, CancellationToken ct) =>
        (await history.ListAsync(query, 100, ct)).Select(Summary).ToArray();

    public static IntegrationJourneyRunSummary Summary(IntegrationJourneyRun run) => new(run.RunId, run.PackId, run.JourneyId, run.ScenarioId, run.EnvironmentId,
        run.OverallState, run.StartedAt, run.CompletedAt, run.Steps.Count(step => step.State == JourneyStepState.Verified), run.Steps.Count, run.Limitations.Count);

    // ── Readiness ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<IntegrationJourneyView> ViewAsync(IIntegrationJourneyPack pack, IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct)
    {
        var prerequisites = new List<JourneyPrerequisite>();
        var trustProblem = TrustProblem(context);
        prerequisites.Add(new JourneyPrerequisite("trusted-environment", "Trusted non-production environment", JourneyPrerequisiteCategory.TrustedEnvironment,
            trustProblem is null ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady, trustProblem ?? context.TrustDetail) { Source = "Target Environment" });

        var matches = Matches(pack, journey, context.Catalog);
        var matched = new List<JourneyMatchedIntegration>();
        foreach (var requirement in journey.Integrations)
        {
            var found = matches.Where(item => item.Requirement.Key == requirement.Key).Select(item => item.Integration!).ToArray();
            var enabled = found.Where(item => item.Enabled).ToArray();
            var enrolled = context.TrustedEnvironment is { } trusted ? enabled.Where(item => trusted.IntegrationIds.Contains(item.Id, StringComparer.Ordinal)).ToArray() : [];
            matched.AddRange(found.Length == 0
                ? [new JourneyMatchedIntegration(requirement.Key, null, null, requirement.Kind, false, $"No configured {requirement.Kind} integration was found for {requirement.Label}.")]
                : found.Select(item =>
                {
                    var match = pack.Match(requirement, item);
                    return new JourneyMatchedIntegration(requirement.Key, item.Id, item.DisplayName, item.Kind, item.Enabled,
                    !item.Enabled ? "Configured in Integration Quality Review but disabled."
                    : context.TrustedEnvironment?.IntegrationIds.Contains(item.Id, StringComparer.Ordinal) == true ? "Configured · enabled for active runs in this environment."
                    : "Configured in Integration Quality Review · not enabled for active runs in this environment.")
                    { MatchState = match.State, MatchedOn = match.Matched, Unresolved = match.Unresolved };
                }));
            // Found ≠ fully mapped: an integration correlated through its canonical identity (channel, resource, producer) with a declared dimension
            // still missing is "Integration found · mapping incomplete" — never "missing", and never a reason to create a duplicate.
            foreach (var item in found.Where(item => item.Enabled))
                if (pack.Match(requirement, item) is { State: IntegrationMatchState.MappingIncomplete } incomplete)
                    prerequisites.Add(new JourneyPrerequisite($"{IntegrationKeyPrefix}{requirement.Key}:mapping", $"{requirement.Label} mapping", JourneyPrerequisiteCategory.Integration,
                        JourneyReadinessState.Partial,
                        $"Integration found: {item.DisplayName} ({string.Join(", ", incomplete.Matched)}) · mapping incomplete: {string.Join("; ", incomplete.Unresolved)}. "
                        + "Expected values come from the Test Pack and are not source verified.", Mandatory: false)
                        {
                            Source = "Integration Quality Review + Test Pack expectation",
                            Diagnostics = [$"integration-id:{item.Id}", .. incomplete.Matched.Select(line => $"Matched: {line}"), .. incomplete.Unresolved.Select(line => $"Unresolved: {line}")],
                        });
            var (state, detail) = found.Length == 0
                ? (JourneyReadinessState.NotConfigured, $"No configured {requirement.Kind} integration was found for {requirement.Label} ({requirement.Producer} → {requirement.Consumer}). Configure it in Integration Quality Review.")
                : enabled.Length == 0 ? (JourneyReadinessState.NotReady, $"{requirement.Label}: the configured integration is disabled.")
                : enrolled.Length == 0 ? (JourneyReadinessState.NotReady, context.TrustedEnvironment is null
                    ? $"{requirement.Label}: configured, but active execution is not enabled for environment {context.EnvironmentId} (technical: TargetEnvironments:Trusted with integration id(s) {string.Join(", ", enabled.Select(item => item.Id))})."
                    : $"{requirement.Label}: configured, but not enabled for active runs in {context.EnvironmentId} (technical: add integration id(s) {string.Join(", ", enabled.Select(item => item.Id))} to its TargetEnvironments:Trusted IntegrationIds — exact ids, no wildcard).")
                : (JourneyReadinessState.Ready, $"{requirement.Label}: {string.Join(", ", enrolled.Select(item => item.DisplayName))}.");
            prerequisites.Add(new JourneyPrerequisite(IntegrationKeyPrefix + requirement.Key, requirement.Label, JourneyPrerequisiteCategory.Integration, state, detail)
                { Source = "Integration Quality Review + Target Environment" });
        }

        var stepStatus = new List<JourneyStepStatus>();
        if (journey.ExecutionMode == JourneyExecutionMode.DelegatedToActiveEvent)
        {
            prerequisites.AddRange(await DelegatedPrerequisitesAsync(journey, context, matches, ct));
            foreach (var step in journey.Steps)
            {
                var configured = step.IntegrationKey is { } key && matches.Any(item => item.Requirement.Key == key && item.Integration is { Enabled: true });
                stepStatus.Add(new(step.StepId, configured, false, false, configured ? JourneyStepState.Configured : JourneyStepState.NotAssessed,
                    "Evidence for this step comes from Active Event Testing runs (transport, consumer activity, continuity, downstream verifier)."));
            }
        }
        else
        {
            var uncovered = new List<string>();
            var firstExecutable = false;
            foreach (var (step, index) in journey.Steps.Select((step, index) => (step, index)))
            {
                var integration = step.IntegrationKey is { } key ? matches.FirstOrDefault(item => item.Requirement.Key == key && item.Integration is { Enabled: true }).Integration : null;
                var probe = new JourneyStepContext(Guid.Empty, context.EnvironmentId, null, journey, null, step, integration, []);
                var coverage = runner.Coverage(probe);
                if (index == 0) firstExecutable = coverage.Executable;
                if (step.Mandatory && !coverage.Executable && !coverage.Observable) uncovered.Add(step.Label);
                var configured = integration is not null;
                stepStatus.Add(new(step.StepId, configured, coverage.Observable || coverage.Executable, coverage.Observable,
                    configured ? JourneyStepState.Configured : JourneyStepState.NotAssessed, coverage.Reason));
            }
            prerequisites.Add(new JourneyPrerequisite("executor", "Journey trigger", JourneyPrerequisiteCategory.Executor,
                firstExecutable ? JourneyReadinessState.Ready : JourneyReadinessState.NotAvailable,
                firstExecutable ? "A registered executor can start the journey." : $"No registered executor can perform the first step (\"{journey.Steps.FirstOrDefault()?.Label}\"); the journey cannot be started.")
                { Source = "BirkNext" });
            // Not a gate: a step without an executor or observer is reported Not assessed in the run, and because Completed needs every
            // mandatory step Verified, a run with uncovered mandatory steps can reach Partial at most. It is shown, never hidden.
            prerequisites.Add(new JourneyPrerequisite("observation", "Step evidence coverage", JourneyPrerequisiteCategory.Observation,
                uncovered.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.Partial,
                uncovered.Count == 0 ? "Every mandatory step has a registered executor or observer."
                    : $"{uncovered.Count} of {journey.Steps.Count(step => step.Mandatory)} mandatory steps have no executor or observer, so a run can reach Partial at most: {string.Join("; ", uncovered)}.",
                Mandatory: false) { Source = "BirkNext" });
        }

        if (context.ResolvedConfiguration is null) context = context with { ResolvedConfiguration = await ResolveConfigurationAsync(pack, context, ct) };
        prerequisites.AddRange(await pack.PrerequisitesAsync(journey, context, ct));
        var readiness = Readiness(prerequisites);
        var lastRun = (await history.ListAsync(new IntegrationJourneyHistoryQuery(context.EnvironmentId, pack.PackId, journey.JourneyId), 1, ct)).FirstOrDefault();
        // Per scenario: a read-only observation does not need what a state-changing submission needs (pack-scoped prerequisites).
        var runnable = journey.ExecutionMode != JourneyExecutionMode.JourneyRunner ? []
            : journey.Scenarios.Where(scenario => scenario.Support == JourneyScenarioSupport.Supported
                    && prerequisites.Where(item => item.Mandatory && item.AppliesTo(scenario.ScenarioId)).All(item => item.State == JourneyReadinessState.Ready))
                .Select(scenario => scenario.ScenarioId).ToArray();
        var canRun = runnable.Length > 0;
        var sourceItems = prerequisites.Where(item => item.Category is JourneyPrerequisiteCategory.SourceEvidence or JourneyPrerequisiteCategory.Contract).ToArray();
        var configuredAll = journey.Integrations.Count > 0 && prerequisites.Where(item => item.Key.StartsWith(IntegrationKeyPrefix, StringComparison.Ordinal)).All(item => item.State == JourneyReadinessState.Ready);
        var sourceVerified = sourceItems.Length > 0 && sourceItems.All(item => item.State == JourneyReadinessState.Ready);
        var runtimeVerified = lastRun?.OverallState == JourneyRunState.Completed;
        var (testDataRequirements, testDataResolution, testDataProblem) = ResolveTestData(pack, journey);
        var journeyBound = JourneyBoundSnapshots(pack, journey, context);
        var dimensions = JourneyReadinessComposer.Compose(new JourneyReadinessInputs(journey, prerequisites, stepStatus, runnable, journeyBound,
            testDataRequirements, testDataResolution, testDataProblem));
        return new IntegrationJourneyView
        {
            Journey = journey, Readiness = readiness, Prerequisites = prerequisites, MatchedIntegrations = matched, StepStatus = stepStatus, CanRun = canRun, RunnableScenarios = runnable,
            ScenarioCapabilities = journey.ExecutionMode == JourneyExecutionMode.DelegatedToActiveEvent
                ? await DelegatedCapabilitiesAsync(journey, context, matches, ct)
                : journey.Scenarios.Select(scenario => Capability(scenario, prerequisites, runnable)).ToArray(),
            LastRun = lastRun is null ? null : Summary(lastRun),
            Dimensions = dimensions, Assessment = JourneyReadinessComposer.Assessment(lastRun?.OverallState),
            SourceArchives = [.. journeyBound.Select(snapshot => snapshot.Archive.FileName).Distinct(StringComparer.OrdinalIgnoreCase)],
            ReadinessSummary = readiness switch
            {
                JourneyReadinessState.Ready => "Ready to run after confirmation.",
                JourneyReadinessState.NotConfigured => "Not configured: a required integration is missing from Integration Quality Review.",
                _ => $"{prerequisites.Count(item => item.Mandatory && item.State == JourneyReadinessState.Ready)} of {prerequisites.Count(item => item.Mandatory)} mandatory prerequisites are ready."
                    + (runnable.Length > 0 ? $" Runnable now: {string.Join(", ", journey.Scenarios.Where(item => runnable.Contains(item.ScenarioId)).Select(item => item.DisplayName))}." : ""),
            },
            Maturity = new JourneyMaturity(configuredAll, sourceVerified, canRun, runtimeVerified,
                $"Configured: {(configuredAll ? "yes" : "no")} · Source verified: {(sourceVerified ? "yes" : "no")} · Executable: {(canRun ? "yes" : "no")} · Runtime verified: {(runtimeVerified ? "yes" : "no")}."),
        };
    }

    /// <summary>Delegated journeys reuse the Active Event readiness of their provider for each matched integration — no second readiness model.</summary>
    private async Task<IReadOnlyList<JourneyPrerequisite>> DelegatedPrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context,
        IReadOnlyList<(JourneyIntegrationRequirement Requirement, IntegrationDefinition? Integration)> matches, CancellationToken ct)
    {
        var result = new List<JourneyPrerequisite>();
        var provider = journey.DelegatedProviderId ?? "";
        var integrations = matches.Where(item => item.Integration is { Enabled: true }).Select(item => item.Integration!).DistinctBy(item => item.Id).ToArray();
        if (integrations.Length == 0)
        {
            result.Add(new("delegated", "Active Event readiness", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotConfigured,
                $"Executed by Active Event Testing (provider {provider}); it has no applicable integration until one is configured."));
            return result;
        }
        foreach (var integration in integrations)
        {
            try
            {
                var scenario = (await activeEvents.ScenariosAsync(context.EnvironmentId, integration.Id, ct))
                    .FirstOrDefault(item => item.ExtensionId == provider && item.Support == ActiveEventScenarioSupport.Supported);
                if (scenario is null)
                {
                    result.Add(new($"delegated:{integration.Id}", $"Active Event provider for {integration.DisplayName}", JourneyPrerequisiteCategory.Executor,
                        JourneyReadinessState.NotAvailable, $"Provider {provider} offers no supported scenario for this integration.")
                        { Diagnostics = await ProviderDiagnosticsAsync(context.EnvironmentId, integration, provider, ct) });
                    continue;
                }
                var readiness = await activeEvents.ReadinessAsync(context.EnvironmentId, integration.Id, provider, scenario.ScenarioId, null, ct);
                result.AddRange(readiness.Checks.Select(check => new JourneyPrerequisite($"active-event:{integration.Id}:{check.Key}", $"{check.Label} ({integration.DisplayName})",
                    Category(check.Category), State(check.State), check.Detail, check.State != ActiveEventReadinessState.Optional)));
            }
            catch (ActiveEventRequestException ex)
            {
                result.Add(new($"delegated:{integration.Id}", $"Active Event readiness for {integration.DisplayName}", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotAvailable, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Why a provider does not apply to an integration, as safe facts: the integration's typed identities, what the provider covers,
    /// which configured integrations it does match and the exact rule that failed.</summary>
    private async Task<IReadOnlyList<string>> ProviderDiagnosticsAsync(string environmentId, IntegrationDefinition integration, string provider, CancellationToken ct)
    {
        var resource = SourceResourceIdentity.Of(integration);
        var lines = new List<string>
        {
            $"Selected integration: {integration.DisplayName} ({integration.Id}) · {integration.Kind}",
            $"Source resource (normalized): {(resource is null ? "not stated — configure database.schema.table or a CDC hub name {prefix}.{database}.{schema}.{table}" : $"{resource.Full} (also {resource.Qualified})")}",
            $"Channel: {integration.EndpointOrTopic ?? "not configured"}",
            $"Provider: {provider}",
        };
        try
        {
            if ((await activeEvents.ProvidersAsync(environmentId, ct)).FirstOrDefault(item => item.ExtensionId == provider) is { } summary)
            {
                lines.Add($"Provider resources: {string.Join(", ", summary.Resources)}");
                lines.AddRange(summary.ResourceStatuses.Select(status => $"{status.Resource}: {(status.State == ActiveEventResourceState.IntegrationFound ? $"integration found ({status.IntegrationName})" : "integration missing")}"));
                lines.Add(summary.ApplicableIntegrationIds.Count == 0 ? "Matching configured integrations: none" : $"Matching configured integrations: {string.Join(", ", summary.ApplicableIntegrationIds)}");
                lines.Add($"Scenarios declared: {summary.Scenarios.Count} ({summary.Scenarios.Count(item => item.Support == ActiveEventScenarioSupport.Supported)} supported)");
            }
        }
        catch (ActiveEventRequestException ex) { lines.Add($"Provider summary unavailable: {ex.Message}"); }
        lines.Add(resource is null ? "Reason: the integration states no source resource the provider can match."
            : integration.Kind != IntegrationKind.EventHub ? "Reason: the provider needs an Event Hub integration."
            : "Reason: the integration's source resource is not one of the provider's resources.");
        return lines;
    }

    /// <summary>
    /// A journey-runner scenario's capability from the readiness already computed for it. Missing configuration (Not configured, Partial) makes it
    /// conditionally runnable; any other unmet mandatory prerequisite blocks it. Operator-required and not-assessed scenarios never run here.
    /// </summary>
    internal static JourneyScenarioCapability Capability(JourneyScenarioDescriptor scenario, IReadOnlyList<JourneyPrerequisite> prerequisites, IReadOnlyList<string> runnable)
    {
        JourneyScenarioCapability Row(JourneyScenarioCapabilityStatus status, string detail, IReadOnlyList<string>? blockers = null) =>
            new(scenario.ScenarioId, scenario.DisplayName, null, status, detail, blockers ?? [], scenario.Capability);
        if (scenario.Capability?.OperatorRequired == true)
            return Row(JourneyScenarioCapabilityStatus.OperatorRequired, scenario.NotAssessedBecause ?? "A human operator must act in the target system; BirkNext does not perform this step.");
        if (scenario.Support != JourneyScenarioSupport.Supported)
            return Row(JourneyScenarioCapabilityStatus.NotAssessed, scenario.NotAssessedBecause ?? "The scenario is not supported.");
        if (runnable.Contains(scenario.ScenarioId, StringComparer.Ordinal))
            return Row(JourneyScenarioCapabilityStatus.Runnable, "Every applicable mandatory prerequisite is ready. Running still needs explicit confirmation.");
        return Blocked(prerequisites.Where(item => item.Mandatory && item.AppliesTo(scenario.ScenarioId) && item.State != JourneyReadinessState.Ready).ToArray(), Row);
    }

    private static JourneyScenarioCapability Blocked(IReadOnlyList<JourneyPrerequisite> unmet,
        Func<JourneyScenarioCapabilityStatus, string, IReadOnlyList<string>?, JourneyScenarioCapability> row)
    {
        var blockers = unmet.Select(item => $"{item.Label}: {item.Detail}").ToArray();
        return unmet.All(item => item.State is JourneyReadinessState.NotConfigured or JourneyReadinessState.Partial)
            ? row(JourneyScenarioCapabilityStatus.ConditionallyRunnable, $"Runs once configured: {string.Join(", ", unmet.Select(item => item.Label))}.", blockers)
            : row(JourneyScenarioCapabilityStatus.Blocked,
                $"Blocked by: {string.Join(", ", unmet.Where(item => item.State is not (JourneyReadinessState.NotConfigured or JourneyReadinessState.Partial)).Select(item => item.Label))}.", blockers);
    }

    /// <summary>
    /// A delegated journey's capabilities come from its Active Event provider's own scenario catalog (no second catalog) and, for an applicable
    /// integration, from Active Event readiness of each supported scenario. Execution happens in Active Event Testing; this only reads.
    /// </summary>
    private async Task<IReadOnlyList<JourneyScenarioCapability>> DelegatedCapabilitiesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context,
        IReadOnlyList<(JourneyIntegrationRequirement Requirement, IntegrationDefinition? Integration)> matches, CancellationToken ct)
    {
        var profile = journey.Scenarios.Select(item => item.Capability).FirstOrDefault(item => item is not null);
        var provider = (await activeEvents.ProvidersAsync(context.EnvironmentId, ct)).FirstOrDefault(item => item.ExtensionId == journey.DelegatedProviderId);
        if (provider is null || provider.Scenarios.Count == 0)
            return journey.Scenarios.Select(scenario => new JourneyScenarioCapability(scenario.ScenarioId, scenario.DisplayName, null, JourneyScenarioCapabilityStatus.Blocked,
                $"Active Event provider {journey.DelegatedProviderId} is not registered, so its scenarios are unknown here.", [], scenario.Capability)).ToArray();

        var integrations = matches.Where(item => item.Integration is { Enabled: true }).Select(item => item.Integration!).DistinctBy(item => item.Id).ToArray();
        var applicable = new Dictionary<string, List<IntegrationDefinition>>(StringComparer.Ordinal);
        foreach (var integration in integrations.Where(item => provider.ApplicableIntegrationIds.Contains(item.Id, StringComparer.Ordinal)))
        {
            try
            {
                foreach (var offered in await activeEvents.ScenariosAsync(context.EnvironmentId, integration.Id, ct))
                    (applicable.TryGetValue(offered.ScenarioId, out var list) ? list : applicable[offered.ScenarioId] = []).Add(integration);
            }
            catch (ActiveEventRequestException) { /* Reported per scenario below as having no applicable integration. */ }
        }

        var result = new List<JourneyScenarioCapability>();
        foreach (var scenario in provider.Scenarios)
        {
            JourneyScenarioCapability Row(JourneyScenarioCapabilityStatus status, string detail, IReadOnlyList<string>? blockers = null) =>
                new(scenario.ScenarioId, scenario.DisplayName, scenario.ResourceLabel, status, detail, blockers ?? [], profile);
            if (scenario.Support != ActiveEventScenarioSupport.Supported)
            {
                result.Add(Row(JourneyScenarioCapabilityStatus.NotAssessed, string.IsNullOrWhiteSpace(scenario.SupportDetail) ? "Not supported by the provider." : scenario.SupportDetail));
                continue;
            }
            if (!applicable.TryGetValue(scenario.ScenarioId, out var targets))
            {
                result.Add(Row(JourneyScenarioCapabilityStatus.ConditionallyRunnable,
                    $"Runs in Active Event Testing once an enabled Event Hub integration for {scenario.ResourceLabel} is configured in Integration Quality Review and enabled for active runs in this environment.",
                    [$"Integration: no applicable integration for {scenario.ResourceLabel}."]));
                continue;
            }
            // Active Event readiness decides whether it can run (CanRun); its unmet checks explain why not. No second rule is applied here.
            var unmet = new List<JourneyPrerequisite>();
            var canRun = false;
            foreach (var integration in targets)
            {
                try
                {
                    var readiness = await activeEvents.ReadinessAsync(context.EnvironmentId, integration.Id, provider.ExtensionId, scenario.ScenarioId, null, ct);
                    canRun |= readiness.CanRun;
                    unmet.AddRange(readiness.Checks.Where(check => check.State is not (ActiveEventReadinessState.Ready or ActiveEventReadinessState.Optional))
                        .Select(check => new JourneyPrerequisite(check.Key, $"{check.Label} ({integration.DisplayName})", Category(check.Category), State(check.State), check.Detail)));
                }
                catch (ActiveEventRequestException ex)
                {
                    unmet.Add(new($"delegated:{integration.Id}", $"Active Event readiness ({integration.DisplayName})", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotAvailable, ex.Message));
                }
            }
            result.Add(canRun
                ? Row(JourneyScenarioCapabilityStatus.Runnable, "Active Event readiness allows a run. Run it from Active Event Testing; it still needs explicit confirmation.",
                    unmet.Select(item => $"{item.Label}: {item.Detail}").ToArray())
                : Blocked(unmet.Count > 0 ? unmet
                    : [new("delegated", "Active Event readiness", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotReady, "Active Event readiness does not allow a run.")], Row));
        }
        return result;
    }

    private static JourneyReadinessState Readiness(IReadOnlyList<JourneyPrerequisite> prerequisites)
    {
        var mandatory = prerequisites.Where(item => item.Mandatory).ToArray();
        if (mandatory.All(item => item.State == JourneyReadinessState.Ready)) return JourneyReadinessState.Ready;
        if (mandatory.Any(item => item.Key.StartsWith(IntegrationKeyPrefix, StringComparison.Ordinal) && item.State == JourneyReadinessState.NotConfigured)) return JourneyReadinessState.NotConfigured;
        return mandatory.Any(item => item.State is JourneyReadinessState.Ready or JourneyReadinessState.Partial) ? JourneyReadinessState.Partial : JourneyReadinessState.NotReady;
    }

    private static JourneyReadinessState State(ActiveEventReadinessState state) => state switch
    {
        ActiveEventReadinessState.Ready => JourneyReadinessState.Ready,
        ActiveEventReadinessState.NotConfigured => JourneyReadinessState.NotConfigured,
        ActiveEventReadinessState.NotAvailable => JourneyReadinessState.NotAvailable,
        ActiveEventReadinessState.Partial or ActiveEventReadinessState.Optional or ActiveEventReadinessState.Unknown => JourneyReadinessState.Partial,
        _ => JourneyReadinessState.NotReady,
    };

    private static JourneyPrerequisiteCategory Category(ActiveEventReadinessCategory category) => category switch
    {
        ActiveEventReadinessCategory.TrustedEnvironment => JourneyPrerequisiteCategory.TrustedEnvironment,
        ActiveEventReadinessCategory.Integration or ActiveEventReadinessCategory.Destination => JourneyPrerequisiteCategory.Integration,
        ActiveEventReadinessCategory.SourceContract or ActiveEventReadinessCategory.CdcCapture => JourneyPrerequisiteCategory.SourceEvidence,
        ActiveEventReadinessCategory.SyntheticData => JourneyPrerequisiteCategory.SyntheticData,
        ActiveEventReadinessCategory.Authentication => JourneyPrerequisiteCategory.Authentication,
        ActiveEventReadinessCategory.Authorization => JourneyPrerequisiteCategory.Authorization,
        ActiveEventReadinessCategory.DownstreamVerification => JourneyPrerequisiteCategory.DownstreamVerification,
        _ => JourneyPrerequisiteCategory.Executor,
    };

    private static IReadOnlyList<(JourneyIntegrationRequirement Requirement, IntegrationDefinition? Integration)> Matches(IIntegrationJourneyPack pack,
        IntegrationJourneyDefinition journey, IntegrationCatalog configured) =>
        journey.Integrations.SelectMany(requirement => configured.Integrations
            .Where(integration => integration.Kind == requirement.Kind && pack.Match(requirement, integration).Found)
            .Select(integration => (requirement, (IntegrationDefinition?)integration))).ToArray();

    /// <summary>Backend-owned trust only: a trusted record, execution allowed, Development/QA type and no production marker on the target host.</summary>
    private static string? TrustProblem(JourneyPackContext context)
    {
        if (context.TrustedEnvironment is not { } trusted) return context.TrustDetail;
        if (!trusted.ExecutionAllowed) return "Trusted, but active execution is not allowed for this environment.";
        if (ActiveEventPolicy.EnvironmentBlock(trusted.EnvironmentType) is { } blocked) return blocked;
        if (Uri.TryCreate(trusted.TargetUrl, UriKind.Absolute, out var url) && ActiveEventPolicy.LooksLikeProduction(url.Host))
            return "The trusted environment's target host carries a production marker.";
        return null;
    }

    private async Task<JourneyPackContext> ContextAsync(string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) throw new IntegrationJourneyRequestException("environmentId is required.");
        var trusted = environments.Resolve(environmentId, out var reason);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return new JourneyPackContext(environmentId, trusted, reason, configured, snapshots, await BindingsAsync(environmentId, ct));
    }

    private async Task<IntegrationJourneyRun> BlockedAsync(IntegrationJourneyRun run, IReadOnlyList<string> reasons, CancellationToken ct)
    {
        var blocked = run with
        {
            OverallState = JourneyRunState.Blocked, CompletedAt = clock.GetUtcNow(), StateReason = "Blocked before execution: " + string.Join(" ", reasons),
            Steps = run.Steps.Select(step => step with { Reason = "Not executed: the run was blocked before execution." }).ToArray(),
            Limitations = [.. run.Limitations, "Nothing was sent, submitted or published by this run."],
        };
        if (!await history.InsertAsync(blocked, ct)) blocked = blocked with { Limitations = [.. blocked.Limitations, "This blocked attempt could not be saved to history."] };
        return blocked;
    }
}
