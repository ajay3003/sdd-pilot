using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.SetupProfiles;

/// <summary>
/// The one setup-profile orchestration: Parse → Validate → Resolve references → Build change plan → Apply approved changes, and Export. It routes
/// every value to its owner (Target Environment catalog, IQR evidence binding, the Test Pack's run configuration) and stores nothing of its own.
/// Validation and preview never mutate. Apply re-plans from the submitted content (stateless) and executes only the selected, safe changes in a
/// fixed order, reporting each operation. Conflicts, ambiguous references, trust and secrets are never applied.
/// </summary>
public sealed class SetupProfileService(IIntegrationJourneyPackRegistry packs, IIntegrationCatalogService catalog,
    Integrations.SourceEvidence.IIntegrationEvidenceBindingStore bindings, SourceAnalysis.IReviewSourceEvidenceProvider sources,
    ITrustedExecutionEnvironmentRegistry environments, ILogger<SetupProfileService> logger, IActiveEventLifecycleService? activeEvents = null)
{
    private const string CatalogOwner = "Target Environment → Integrations";
    private const string BindingOwner = "Integration Quality Review → evidence binding";
    private const string RunOwner = "Test pack run configuration";
    public static readonly IReadOnlyList<string> AlwaysManual = ["trustedExecution", "authenticationSecrets", "approvedTestIdentity", "authorizationAttestation"];

    /// <summary>A planned change and how to apply it (null for nothing to apply).</summary>
    private sealed record Planned(SetupChange Change, Func<CancellationToken, Task<string>>? Apply);

    private sealed record Plan(SetupProfilePreview Preview, IReadOnlyList<Planned> Items);

    public async Task<SetupProfilePreview> PreviewAsync(string environmentId, string packId, SetupProfileRequest request, CancellationToken ct) =>
        (await PlanAsync(environmentId, packId, request.FileName, request.Content, ct)).Preview;

    public async Task<SetupApplyResult> ApplyAsync(string environmentId, string packId, SetupApplyRequest request, CancellationToken ct)
    {
        var plan = await PlanAsync(environmentId, packId, request.FileName, request.Content, ct);
        if (!plan.Preview.Valid) return new SetupApplyResult { Issues = plan.Preview.Issues };
        var selected = request.SelectedChangeIds.ToHashSet(StringComparer.Ordinal);
        var confirmed = (request.ConfirmedChangeIds ?? []).ToHashSet(StringComparer.Ordinal);
        var results = new List<SetupApplyItemResult>();
        foreach (var item in plan.Items)
        {
            var change = item.Change;
            if (!selected.Contains(change.Id))
            {
                if (change.Classification == SetupChangeClassification.Same) results.Add(new(change.Id, change.Setting, SetupApplyOutcome.AlreadyMatched, "Already configured."));
                continue;
            }
            // Never: conflicts, things that cannot be imported, reference-only items, or anything needing a confirmation that was not given.
            if (change.Classification is SetupChangeClassification.Conflict or SetupChangeClassification.CannotImport or SetupChangeClassification.ReferenceOnly
                || item.Apply is null || !change.SafeToApply || change.RequiresConfirmation && !confirmed.Contains(change.Id))
            {
                results.Add(new(change.Id, change.Setting, change.Classification == SetupChangeClassification.Same ? SetupApplyOutcome.AlreadyMatched : SetupApplyOutcome.Refused,
                    change.Classification switch
                    {
                        SetupChangeClassification.Conflict => "Conflict left unchanged: the configured value is never overwritten by an import.",
                        SetupChangeClassification.Same => "Already configured.",
                        _ when change.RequiresConfirmation && !confirmed.Contains(change.Id) => "Requires explicit confirmation; nothing was changed.",
                        _ => change.Reason,
                    }));
                continue;
            }
            try { results.Add(new(change.Id, change.Setting, SetupApplyOutcome.Applied, await item.Apply(ct))); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException or Microsoft.EntityFrameworkCore.DbUpdateException)
            { results.Add(new(change.Id, change.Setting, SetupApplyOutcome.Failed, ex.Message)); }
        }
        // Audit without values: counts only (rejected content is never logged).
        logger.LogInformation("Setup profile {File} (v{Version}) applied to {Environment}/{Pack}: {Applied} applied, {Matched} matched, {Conflicts} conflict(s) left, {Failed} failed, {Rejected} rejected field(s).",
            System.IO.Path.GetFileName(request.FileName), plan.Preview.Version, environmentId, packId, results.Count(r => r.Outcome == SetupApplyOutcome.Applied),
            results.Count(r => r.Outcome == SetupApplyOutcome.AlreadyMatched), plan.Preview.Summary.Conflicts, results.Count(r => r.Outcome == SetupApplyOutcome.Failed), plan.Preview.Summary.Rejected);
        return new SetupApplyResult
        {
            Items = results, ConflictsLeft = plan.Preview.Summary.Conflicts, ManualRemaining = plan.Preview.ManualRequirements.Count, Issues = plan.Preview.Issues,
        };
    }

    private async Task<Plan> PlanAsync(string environmentId, string packId, string fileName, string content, CancellationToken ct)
    {
        var (root, format, issues) = SetupProfileParser.Parse(fileName, content ?? "");
        if (root is not null) issues.AddRange(SetupProfileSchema.Validate(root));
        var pack = packs.Find(packId);
        var declaredPack = root?["profile"]?.Str("testPack");
        if (pack is null) issues.Add(new("profile.testPack", SetupIssueSeverity.Error, $"Test pack \"{packId}\" is not registered."));
        else if (root is not null && declaredPack is not null && !string.Equals(declaredPack, packId, StringComparison.OrdinalIgnoreCase))
            issues.Add(new("profile.testPack", SetupIssueSeverity.Error, $"The profile is for test pack \"{declaredPack}\", not {packId}."));
        if (root?["journeys"] is { IsMap: true } journeysNode && pack is not null)
            foreach (var (id, _) in journeysNode.Map!)
                if (pack.Journeys.All(journey => !string.Equals(journey.JourneyId, id, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new($"journeys.{id}", SetupIssueSeverity.Error, $"Journey \"{id}\" is not part of test pack {packId} ({string.Join(", ", pack.Journeys.Select(j => j.JourneyId))})."));
        var valid = root is not null && pack is not null && issues.All(issue => issue.Severity != SetupIssueSeverity.Error);
        var basic = new SetupProfilePreview
        {
            Valid = valid, Format = format, ProfileName = root?["profile"]?.Str("name"), TestPack = declaredPack,
            Version = int.TryParse(root?.Str("version"), out var version) ? version : null, Issues = issues,
        };
        if (!valid) return new Plan(basic with { Summary = Summarize([], issues, 0) }, []);

        var trusted = environments.Resolve(environmentId, out var trustReason);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        var bound = await bindings.ListAsync(environmentId, ct);
        var context = new JourneyPackContext(environmentId, trusted, trustReason, configured, snapshots, bound);
        var runValues = await pack!.CurrentRunValuesAsync(context, ct);

        // Environment portability: a profile for another environment type needs confirmation; a production target refuses everything.
        var profileType = root!["profile"]?.Str("environmentType") ?? root["profile"]?.Str("environment");
        var currentType = trusted?.EnvironmentType;
        var production = string.Equals(currentType, "Production", StringComparison.OrdinalIgnoreCase);
        string? notice = production ? "The current environment is Production: a setup profile is never applied to Production."
            : profileType is not null && currentType is not null && !string.Equals(profileType, currentType, StringComparison.OrdinalIgnoreCase)
                ? $"Profile targets {profileType} · current environment is {currentType}. Every change requires confirmation." : null;
        var mismatch = notice is not null;

        var items = new List<Planned>();
        foreach (var journey in pack.Journeys)
        {
            if (root["journeys"]?[journey.JourneyId] is not { IsMap: true } entry) continue;
            var requirement = journey.Integrations.FirstOrDefault();
            if (requirement is not null) items.AddRange(await IntegrationChangesAsync(journey, requirement, entry, pack, configured, snapshots, bound, environmentId, issues, ct));
            foreach (var field in pack.SetupRunFields.Where(f => f.JourneyId == journey.JourneyId))
                if (Node(entry, field.Path) is { Scalar: { } value } node && SetupProfileSchema.Usable(node.Path, issues))
                    items.Add(RunChange(journey.JourneyId, field, value, runValues.GetValueOrDefault(field.ApplyKey), pack, context));
            if (entry["executionProvider"] is { IsMap: true } provider)
                items.Add(await ProviderChangeAsync(journey.JourneyId, provider, environmentId, ct));
        }
        foreach (var (alias, index) in (root["aliases"]?.Items ?? []).Select((node, index) => (node, index)))
            if (alias.Str("logical") is { } logical && alias.Str("technical") is { } technical)
            {
                var known = pack.Aliases.Any(item => IntegrationIdentityResolver.SameName(item.Logical, logical) && IntegrationIdentityResolver.SameName(item.Technical, technical));
                items.Add(new(new SetupChange
                {
                    Id = $"aliases.{index}", Owner = "Test Pack (expected aliases)", Setting = $"Alias {logical} ↔ {technical}", Proposed = $"{logical} ↔ {technical}",
                    Kind = SetupValueKind.ExpectedMapping, Classification = known ? SetupChangeClassification.Same : SetupChangeClassification.ReferenceOnly,
                    Provenance = known ? "Test Pack expected alias" : "Imported expected mapping",
                    Reason = known ? "The Test Pack already expects this alias (expected, not source verified)."
                        : "Reference only: BirkNext keeps no alias store; an expected mapping never becomes source verified. The evidence layer may later show it as source supported.",
                }, null));
            }
        if (root["securityClassification"] is { IsMap: true } security)
            items.Add(new(new SetupChange
            {
                Id = "securityClassification", Owner = "Security Classification (M01) · Test Data", Setting = "M01 security test references",
                Proposed = string.Join(", ", new[] { security.Str("environment"), security["personGraphql"]?.Str("endpointRef") }.Concat(security["testData"]?.Map?.Select(e => e.Value.Scalar) ?? []).OfType<string>()),
                Kind = SetupValueKind.ReferenceMetadata, Classification = SetupChangeClassification.ReferenceOnly, Provenance = "Imported setup profile",
                Reason = "Reference only: M01 resolves its context from Target Environment, Endpoint Discovery and approved Test Data aliases; test-lead approval is never imported.",
            }, null));

        // Portability: a mismatch turns every applicable change into one that needs explicit confirmation; Production refuses everything.
        items = [.. items.Select(item => production && item.Apply is not null
            ? item with { Change = item.Change with { Classification = SetupChangeClassification.CannotImport, SafeToApply = false, Preselected = false, Reason = notice! }, Apply = null }
            : mismatch && item.Change.Classification == SetupChangeClassification.New
                ? item with { Change = item.Change with { Classification = SetupChangeClassification.RequiresConfirmation, RequiresConfirmation = true, Preselected = false } }
                : item)];
        var manual = AlwaysManual.Concat(root["manualRequirements"]?.Items?.Select(node => node.Scalar).OfType<string>() ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changes = items.Select(item => item.Change).ToList();
        return new Plan(basic with { Changes = changes, ManualRequirements = manual, EnvironmentNotice = notice, Summary = Summarize(changes, issues, manual.Count) }, items);
    }

    private static SetupProfileSummary Summarize(IReadOnlyList<SetupChange> changes, IReadOnlyList<SetupProfileIssue> issues, int manual) => new(
        changes.Count(c => c.Classification != SetupChangeClassification.ReferenceOnly), changes.Count(c => c.Preselected),
        changes.Count(c => c.Classification == SetupChangeClassification.Same), changes.Count(c => c.Classification == SetupChangeClassification.Conflict),
        changes.Count(c => c.Classification == SetupChangeClassification.RequiresConfirmation), changes.Count(c => c.Classification == SetupChangeClassification.CannotImport),
        changes.Count(c => c.Classification == SetupChangeClassification.ReferenceOnly), issues.Count(i => i.Severity == SetupIssueSeverity.Rejected), manual);

    private static SetupNode? Node(SetupNode entry, string path) => path.Split('.').Aggregate((SetupNode?)entry, (node, key) => node?[key]);

    private static readonly Dictionary<string, IntegrationKind> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["http-api"] = IntegrationKind.HttpApi, ["event-hub"] = IntegrationKind.EventHub, ["service-bus"] = IntegrationKind.ServiceBus,
    };

    /// <summary>The integration as the profile describes it (only usable, non-rejected values), mapped onto IntegrationDefinition fields.</summary>
    private static Dictionary<string, (string Label, string Value)> IntegrationFields(SetupNode entry, IReadOnlyList<SetupProfileIssue> issues)
    {
        var fields = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        void Put(string field, string label, params SetupNode?[] candidates)
        {
            if (candidates.FirstOrDefault(node => node?.Scalar is { Length: > 0 } && SetupProfileSchema.Usable(node.Path, issues)) is { Scalar: { } value }) fields[field] = (label, value.Trim());
        }
        var integration = entry["integration"];
        var contracts = entry["contracts"];
        Put(nameof(IntegrationDefinition.EndpointOrTopic), "Endpoint / channel", integration?["channel"]?["name"], integration?["channel"], integration?["endpoint"]);
        Put(nameof(IntegrationDefinition.Producer), "Producer", integration?["producerImplementation"], integration?["producer"]?["component"], integration?["producer"], integration?["producer"]?["technology"]);
        Put(nameof(IntegrationDefinition.SourceSystem), "Producer role", integration?["producerRole"], integration?["producer"]?["logicalName"]);
        Put("Consumer", "Consumer", integration?["consumer"]?["component"], integration?["consumer"], integration?["consumer"]?["logicalName"]);
        Put(nameof(IntegrationDefinition.DestinationSystem), "Consumer role", integration?["consumer"]?["logicalName"]);
        Put(nameof(IntegrationDefinition.ProducerContractReference), "Producer contract", contracts?["producer"]?["reference"]);
        Put(nameof(IntegrationDefinition.ConsumerContractReference), "Consumer contract", contracts?["consumer"]?["sourcePath"]);
        if (integration?["sourceResource"] is { } resource && SetupProfileSchema.Usable(resource.Path, issues))
        {
            var text = resource.IsMap ? string.Join('.', new[] { resource.Str("database"), resource.Str("schema"), resource.Str("table") }.Where(part => !string.IsNullOrWhiteSpace(part))) : resource.Scalar;
            if (text is { Length: > 0 }) fields[nameof(IntegrationDefinition.SourceResource)] = ("Source resource", text);
        }
        return fields;
    }

    private static string? Current(IntegrationDefinition integration, string field) => field switch
    {
        nameof(IntegrationDefinition.EndpointOrTopic) => integration.EndpointOrTopic,
        nameof(IntegrationDefinition.Producer) => integration.Producer,
        nameof(IntegrationDefinition.SourceSystem) => integration.SourceSystem,
        "Consumer" => integration.Consumer.DisplayName,
        nameof(IntegrationDefinition.DestinationSystem) => integration.DestinationSystem,
        nameof(IntegrationDefinition.ProducerContractReference) => integration.ProducerContractReference,
        nameof(IntegrationDefinition.ConsumerContractReference) => integration.ConsumerContractReference,
        nameof(IntegrationDefinition.SourceResource) => integration.SourceResource,
        _ => null,
    };

    private static IntegrationDefinition With(IntegrationDefinition integration, string field, string value) => field switch
    {
        nameof(IntegrationDefinition.EndpointOrTopic) => integration with { EndpointOrTopic = value },
        nameof(IntegrationDefinition.Producer) => integration with { Producer = value },
        nameof(IntegrationDefinition.SourceSystem) => integration with { SourceSystem = value },
        // Configured from the profile; the mapping is never confirmed by an import.
        "Consumer" => integration with { Consumer = integration.Consumer with { DisplayName = value, MappingState = integration.Consumer.MappingState == ConsumerMappingState.Confirmed ? ConsumerMappingState.Confirmed : ConsumerMappingState.NeedsConfirmation } },
        nameof(IntegrationDefinition.DestinationSystem) => integration with { DestinationSystem = value },
        nameof(IntegrationDefinition.ProducerContractReference) => integration with { ProducerContractReference = value },
        nameof(IntegrationDefinition.ConsumerContractReference) => integration with { ConsumerContractReference = value },
        nameof(IntegrationDefinition.SourceResource) => integration with { SourceResource = value },
        _ => integration,
    };

    private async Task<IReadOnlyList<Planned>> IntegrationChangesAsync(IntegrationJourneyDefinition journey, JourneyIntegrationRequirement requirement, SetupNode entry,
        IIntegrationJourneyPack pack, IntegrationCatalog configured, IReadOnlyList<IqrSourceSnapshot> snapshots, IReadOnlyList<IntegrationEvidenceBinding> bound,
        string environmentId, List<SetupProfileIssue> issues, CancellationToken ct)
    {
        var result = new List<Planned>();
        var prefix = journey.JourneyId;
        var fields = IntegrationFields(entry, issues);
        if (entry["integration"]?.Str("type") is { } type && (!Types.TryGetValue(type, out var kind) || kind != requirement.Kind))
        {
            issues.Add(new($"journeys.{prefix}.integration.type", SetupIssueSeverity.Error, Types.ContainsKey(type)
                ? $"Journey {prefix} needs a {requirement.Kind} integration, not {type}." : $"Integration type \"{type}\" is not supported (http-api, event-hub, service-bus)."));
            return result;
        }
        if (fields.TryGetValue(nameof(IntegrationDefinition.EndpointOrTopic), out var endpoint) && requirement.Kind == IntegrationKind.HttpApi
            && !(Uri.TryCreate(endpoint.Value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
        { issues.Add(new($"journeys.{prefix}.integration.endpoint", SetupIssueSeverity.Error, $"\"{SetupProfileSchema.Safe(endpoint.Value)}\" is not an absolute http(s) URL.")); fields.Remove(nameof(IntegrationDefinition.EndpointOrTopic)); }
        if (fields.TryGetValue(nameof(IntegrationDefinition.SourceResource), out var resource) && SourceResourceIdentity.Parse(resource.Value) is null)
        { issues.Add(new($"journeys.{prefix}.integration.sourceResource", SetupIssueSeverity.Error, $"\"{SetupProfileSchema.Safe(resource.Value)}\" is not schema.table or database.schema.table.")); fields.Remove(nameof(IntegrationDefinition.SourceResource)); }

        // Resolve against existing integrations through the canonical identity — the profile's own channel/resource/names widen it, never replace it.
        var identity = requirement.Identity ?? new IntegrationRequirementIdentity { Kind = requirement.Kind };
        identity = identity with
        {
            Channels = [.. identity.Channels, .. fields.TryGetValue(nameof(IntegrationDefinition.EndpointOrTopic), out var channel) ? new[] { channel.Value } : []],
            SourceResources = [.. identity.SourceResources, .. fields.TryGetValue(nameof(IntegrationDefinition.SourceResource), out var r) && SourceResourceIdentity.Parse(r.Value) is { } parsed ? new[] { parsed } : []],
        };
        var matches = configured.Integrations.Where(item => IntegrationIdentityResolver.Match(identity, item).Found || pack.Match(requirement, item).Found).ToList();
        var logical = entry["integration"]?.Str("logicalName") ?? requirement.Label;
        if (matches.Count > 1)
        {
            result.Add(new(new SetupChange
            {
                Id = $"{prefix}.integration", Journey = prefix, Owner = CatalogOwner, Setting = $"Integration for {logical}", Proposed = logical,
                Current = string.Join(", ", matches.Select(item => item.DisplayName)), Kind = SetupValueKind.DesiredConfiguration,
                Classification = SetupChangeClassification.CannotImport, Reason = $"{matches.Count} configured integrations match; resolve which one represents {logical} first (nothing is duplicated).",
                Provenance = "Imported setup profile", Destination = CatalogOwner,
            }, null));
            return result;
        }
        IntegrationDefinition? target = matches.SingleOrDefault();
        if (target is null)
        {
            var platform = requirement.Kind == IntegrationKind.HttpApi ? null : configured.Platforms.Where(p => p.Kind == requirement.Kind).ToList() is { Count: 1 } one ? one[0] : null;
            var canCreate = requirement.Kind == IntegrationKind.HttpApi || platform is not null;
            var definition = fields.Aggregate(new IntegrationDefinition
            {
                EnvironmentId = environmentId, DisplayName = logical, Kind = requirement.Kind, PlatformId = platform?.Id, Enabled = true, Origin = IntegrationRecordOrigin.Manual,
                Consumer = new IntegrationConsumer { MappingState = ConsumerMappingState.NeedsConfirmation },
            }, (definition, field) => With(definition, field.Key, field.Value.Value));
            result.Add(new(new SetupChange
            {
                Id = $"{prefix}.integration", Journey = prefix, Owner = CatalogOwner, Setting = $"Create integration {logical}", Proposed = $"{logical} ({requirement.Kind})",
                Kind = SetupValueKind.DesiredConfiguration, Provenance = "Imported setup profile", Destination = CatalogOwner,
                Classification = canCreate ? SetupChangeClassification.RequiresConfirmation : SetupChangeClassification.CannotImport, SafeToApply = canCreate, RequiresConfirmation = true,
                Reason = canCreate ? "No configured integration matches its canonical identity (channel, resource, producer/consumer, aliases); creating one needs your confirmation. Its mapping is not confirmed."
                    : $"No configured integration matches and no single {requirement.Kind} platform exists to create it on; configure it in Target Environment.",
            }, canCreate ? async token => $"Created {(await catalog.CreateAsync(environmentId, definition, token)).DisplayName}." : null));
            return result;
        }

        foreach (var (field, (label, value)) in fields)
        {
            var current = Current(target, field);
            var same = string.Equals(current?.Trim(), value, StringComparison.OrdinalIgnoreCase)
                || field == nameof(IntegrationDefinition.SourceResource) && SourceResourceIdentity.Parse(current) is { } a && SourceResourceIdentity.Parse(value) is { } b && a.SameResource(b) && (a.Database ?? b.Database) is not null && a.Full.Equals(b.Full, StringComparison.OrdinalIgnoreCase);
            var classification = same ? SetupChangeClassification.Same : string.IsNullOrWhiteSpace(current) ? SetupChangeClassification.New : SetupChangeClassification.Conflict;
            var targetId = target.Id;
            result.Add(new(new SetupChange
            {
                Id = $"{prefix}.integration.{field}", Journey = prefix, Owner = CatalogOwner, Setting = $"{target.DisplayName} · {label}", Current = current, Proposed = value,
                Kind = field is "Consumer" or nameof(IntegrationDefinition.DestinationSystem) ? SetupValueKind.ExpectedMapping : SetupValueKind.DesiredConfiguration,
                Classification = classification, SafeToApply = classification == SetupChangeClassification.New, Preselected = classification == SetupChangeClassification.New,
                Provenance = $"Current: {CatalogOwner} · proposed: imported setup profile{(field == "Consumer" ? " (configured, not source verified; mapping stays unconfirmed)" : "")}",
                Reason = classification switch
                {
                    SetupChangeClassification.Same => "Already configured.",
                    SetupChangeClassification.Conflict => "Conflict · manual review required: the configured value is never overwritten by an import.",
                    _ => "Empty today; the profile's value fills it.",
                },
                Destination = $"{CatalogOwner} · {target.DisplayName}",
            }, classification == SetupChangeClassification.New ? async token =>
            {
                var fresh = (await catalog.GetAsync(environmentId, null, null, token)).Integrations.FirstOrDefault(item => item.Id == targetId)
                    ?? throw new InvalidOperationException("The integration no longer exists.");
                if (!string.IsNullOrWhiteSpace(Current(fresh, field))) throw new InvalidOperationException("The value was set meanwhile; it is left unchanged.");
                await catalog.UpdateAsync(environmentId, targetId, With(fresh, field, value), token);
                return $"{label} set on {fresh.DisplayName}.";
            } : null));
        }

        // Proposed source binding: exact snapshot id, else the archive name — one stored snapshot only; never the newest of several.
        if (entry["sourceEvidence"] is { IsMap: true } evidence)
        {
            var byId = Guid.TryParse(evidence.Str("snapshotId"), out var snapshotId) ? snapshots.Where(s => s.Id == snapshotId).ToList() : null;
            var name = evidence.Str("snapshotName");
            var candidates = byId ?? (name is null ? [] : snapshots.Where(s => string.Equals(s.Archive.FileName, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList());
            var current = bound.FirstOrDefault(binding => binding.IntegrationId == target.Id);
            var label = name ?? evidence.Str("snapshotId") ?? "";
            SetupChangeClassification classification; string reason; IqrSourceSnapshot? chosen = null;
            if (candidates.Count == 0) { classification = SetupChangeClassification.CannotImport; reason = $"Journey \"{prefix}\" references source snapshot \"{label}\", but no matching Source Analysis snapshot exists. Import it first."; }
            else if (candidates.Count > 1) { classification = SetupChangeClassification.RequiresConfirmation; reason = $"{candidates.Count} snapshots are named {label} ({candidates.Select(c => c.Archive.Sha256).Distinct().Count()} distinct contents). Choose the exact one in Integration Quality Review → Evidence; the newest is never chosen."; }
            else if (current is not null && (current.SnapshotId == candidates[0].Id || current.Fingerprint == candidates[0].Archive.Sha256)) { classification = SetupChangeClassification.Same; reason = "Already bound."; }
            else if (current is not null) { classification = SetupChangeClassification.Conflict; reason = $"Conflict · manual review required: {target.DisplayName} is bound to {current.ArchiveName}; the binding is changed only in IQR Evidence, never by an import."; }
            else { classification = SetupChangeClassification.New; chosen = candidates[0]; reason = "Matched exactly one stored snapshot; binding makes it this integration's canonical source evidence."; }
            var targetId = target.Id;
            result.Add(new(new SetupChange
            {
                Id = $"{prefix}.sourceEvidence", Journey = prefix, Owner = BindingOwner, Setting = $"{target.DisplayName} · canonical source evidence", Current = current?.ArchiveName, Proposed = label,
                Kind = SetupValueKind.ProposedSourceBinding, Classification = classification, SafeToApply = classification == SetupChangeClassification.New,
                Preselected = classification == SetupChangeClassification.New, Provenance = $"Current: {BindingOwner} · proposed: imported setup profile", Reason = reason,
                Destination = $"{BindingOwner} · {target.DisplayName}",
            }, chosen is null ? null : async token =>
            {
                var (binding, error) = await bindings.SetAsync(environmentId, new IntegrationEvidenceBindingRequest(targetId, chosen.Id), token);
                return binding is null ? throw new InvalidOperationException(error ?? "The binding was refused.") : $"Bound {binding.ArchiveName}.";
            }));
        }
        return result;
    }

    private static Planned RunChange(string journeyId, SetupRunField field, string value, string? current, IIntegrationJourneyPack pack, JourneyPackContext context)
    {
        var invalid = field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)
            ? $"\"{SetupProfileSchema.Safe(value)}\" is not allowed (allowed: {string.Join(", ", field.AllowedValues)})."
            : field.Url && !(Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https") ? $"\"{SetupProfileSchema.Safe(value)}\" is not an absolute https URL." : null;
        var normalized = field.AllowedValues.FirstOrDefault(allowed => allowed.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();
        var classification = invalid is not null ? SetupChangeClassification.CannotImport
            : string.Equals(current?.Trim(), normalized, StringComparison.OrdinalIgnoreCase) ? SetupChangeClassification.Same
            : string.IsNullOrWhiteSpace(current) ? SetupChangeClassification.New : SetupChangeClassification.Conflict;
        return new(new SetupChange
        {
            Id = $"{journeyId}.{field.Path}", Journey = journeyId, Owner = RunOwner, Setting = field.Label, Current = current, Proposed = normalized,
            Kind = SetupValueKind.DesiredConfiguration, Classification = classification, SafeToApply = classification == SetupChangeClassification.New,
            Preselected = classification == SetupChangeClassification.New, Provenance = $"Current: {RunOwner} · proposed: imported setup profile (saved for active run · not runtime verified)",
            Reason = invalid ?? classification switch
            {
                SetupChangeClassification.Same => "Already saved for active run.",
                SetupChangeClassification.Conflict => "Conflict · manual review required: a saved run value is never overwritten by an import.",
                _ => "Empty today; only empty run-configuration fields are filled.",
            },
            Destination = RunOwner,
        }, classification == SetupChangeClassification.New ? async token =>
        {
            var applied = await pack.ApplyConfigurationAsync(context, new Dictionary<string, string> { [field.ApplyKey] = normalized }, token);
            return applied.Count > 0 ? $"{field.Label} saved for active run (not runtime verified)." : throw new InvalidOperationException("The run configuration already has a value; nothing was overwritten.");
        } : null);
    }

    private async Task<Planned> ProviderChangeAsync(string journeyId, SetupNode provider, string environmentId, CancellationToken ct)
    {
        var providerId = provider.Str("providerId") ?? "";
        var resource = provider.Str("resource") ?? "";
        string detail;
        try
        {
            var summary = activeEvents is null ? null : (await activeEvents.ProvidersAsync(environmentId, ct)).FirstOrDefault(item => item.ExtensionId == providerId);
            var status = summary?.ResourceStatuses.FirstOrDefault(item => SourceResourceIdentity.Parse(item.Resource) is { } a && SourceResourceIdentity.Parse(resource) is { } b && a.SameResource(b));
            detail = summary is null ? $"Provider {providerId} is not registered."
                : status is null ? $"Provider {providerId} does not cover {resource} ({string.Join(", ", summary.Resources)})."
                : status.State == ActiveEventResourceState.IntegrationFound ? $"{resource}: integration found ({status.IntegrationName}) — resolved from the typed source-resource identity."
                : $"{resource}: no configured integration names it yet.";
        }
        catch (Exception ex) when (ex is ActiveEventRequestException or InvalidOperationException) { detail = $"Provider readiness unavailable: {ex.Message}"; }
        return new(new SetupChange
        {
            Id = $"{journeyId}.executionProvider", Journey = journeyId, Owner = "Active Event provider", Setting = $"Execution provider {providerId}", Proposed = $"{providerId} · {resource}",
            Kind = SetupValueKind.ReferenceMetadata, Classification = SetupChangeClassification.ReferenceOnly, Provenance = "Imported setup profile",
            Reason = $"Reference only: provider mapping is resolved from the integration's resource identity, not stored. {detail}",
        }, null);
    }

    // ── Export ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static readonly IReadOnlyList<string> ExportExcluded =
        ["Secrets, credentials, tokens and private keys", "Personal identifiers and test identities", "Execution trust and active-execution permission",
         "Authorization attestations and test-lead approval", "Runtime results, health and run history", "Mapping and contract confirmations"];

    public async Task<SetupProfileExport> ExportAsync(string environmentId, string packId, SetupProfileFormat format, CancellationToken ct)
    {
        var pack = packs.Find(packId) ?? throw new IntegrationJourneyRequestException($"Test pack \"{packId}\" is not registered.");
        var trusted = environments.Resolve(environmentId, out var trustReason);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        var bound = await bindings.ListAsync(environmentId, ct);
        var context = new JourneyPackContext(environmentId, trusted, trustReason, configured, snapshots, bound);
        var run = await pack.CurrentRunValuesAsync(context, ct);
        var journeys = new Dictionary<string, object?>();
        foreach (var journey in pack.Journeys)
        {
            var section = new Dictionary<string, object?>();
            if (journey.Integrations.FirstOrDefault() is { } requirement
                && configured.Integrations.FirstOrDefault(item => pack.Match(requirement, item).Found) is { } integration)
            {
                section["integration"] = Compact(new Dictionary<string, object?>
                {
                    ["logicalName"] = integration.DisplayName, ["type"] = Types.FirstOrDefault(t => t.Value == integration.Kind).Key,
                    [integration.Kind == IntegrationKind.HttpApi ? "endpoint" : "channel"] = integration.EndpointOrTopic,
                    ["producer"] = integration.Producer, ["producerRole"] = integration.SourceSystem,
                    ["consumer"] = Compact(new Dictionary<string, object?> { ["component"] = integration.Consumer.DisplayName, ["logicalName"] = integration.DestinationSystem }),
                    ["sourceResource"] = integration.SourceResource,
                });
                if (bound.FirstOrDefault(binding => binding.IntegrationId == integration.Id) is { } binding)
                    section["sourceEvidence"] = new Dictionary<string, object?> { ["snapshotName"] = binding.ArchiveName, ["snapshotId"] = binding.SnapshotId.ToString() };
                section["contracts"] = Compact(new Dictionary<string, object?>
                {
                    ["producer"] = integration.ProducerContractReference is { } producer ? new Dictionary<string, object?> { ["reference"] = producer } : null,
                    ["consumer"] = integration.ConsumerContractReference is { } consumer ? new Dictionary<string, object?> { ["sourcePath"] = consumer } : null,
                });
            }
            foreach (var field in pack.SetupRunFields.Where(f => f.JourneyId == journey.JourneyId))
                if (run.GetValueOrDefault(field.ApplyKey) is { Length: > 0 } value)
                {
                    var parts = field.Path.Split('.');
                    var container = section;
                    foreach (var part in parts[..^1]) container = (Dictionary<string, object?>)(container.TryGetValue(part, out var existing) && existing is Dictionary<string, object?> map ? map : container[part] = new Dictionary<string, object?>());
                    container[parts[^1]] = value;
                }
            if (Compact(section) is { Count: > 0 } compact) journeys[journey.JourneyId] = compact;
        }
        var document = new Dictionary<string, object?>
        {
            ["version"] = SetupProfileSchema.SupportedVersion,
            ["profile"] = Compact(new Dictionary<string, object?>
            {
                ["name"] = $"{pack.DisplayName} {trusted?.DisplayName ?? environmentId}", ["testPack"] = pack.PackId, ["environment"] = trusted?.DisplayName ?? environmentId,
                ["environmentType"] = trusted?.EnvironmentType,
            }),
            ["metadata"] = new Dictionary<string, object?> { ["generatedBy"] = "BirkNext", ["sanitized"] = true, ["containsSecrets"] = false },
            ["journeys"] = journeys,
            ["aliases"] = pack.Aliases.Select(alias => (object?)new Dictionary<string, object?> { ["logical"] = alias.Logical, ["technical"] = alias.Technical }).ToList(),
            ["manualRequirements"] = AlwaysManual.Cast<object?>().ToList(),
        };
        var text = format == SetupProfileFormat.Json ? JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) : Yaml(document, 0);
        return new SetupProfileExport(format, $"{pack.PackId}-{Slug(trusted?.DisplayName ?? environmentId)}.setup.{(format == SetupProfileFormat.Json ? "json" : "yaml")}", text, ExportExcluded);
    }

    /// <summary>A safe example with placeholders (no secrets, nothing confirmed), for teams to start a baseline from.</summary>
    public static string Example(string packId) => $$"""
        # Example setup profile (placeholders; no secrets). Importing it previews changes first; nothing is applied without your selection.
        version: 1
        profile:
          name: "Example Development baseline"
          testPack: "{{packId}}"
          environment: "Development"
          environmentType: "Development"
        metadata:
          generatedBy: "BirkNext example"
          sanitized: true
          containsSecrets: false
        journeys:
          rapportmottak:
            integration:
              logicalName: "Skolenærvær – Altinn report intake"
              type: "http-api"
              producer: "Altinn"
              consumer: "Meldingsutveksleren (MU)"
              endpoint: "https://example.invalid/api/altinn/webhook"
            sourceEvidence:
              snapshotName: "<MU source archive>.zip"
            altinn:
              environment: "Test"
              appId: "<org>/<app>"
              baseUrl: "https://<altinn-test-app-host>/<org>/<app>"
          utdanningsdata:
            integration:
              logicalName: "Skolenærvær – Education CDC"
              type: "event-hub"
              sourceResource:
                database: "<Database>"
                schema: "dbo"
                table: "Utdanning"
            executionProvider:
              providerId: "skolenaervaer.cdc"
              resource: "dbo.Utdanning"
          periodeavslutning:
            integration:
              logicalName: "Skolenærvær – Period Closed"
              type: "service-bus"
              producerRole: "Skolenærværsrapport"
              channel:
                name: "skole.reports"
              consumer:
                logicalName: "Bufdata"
        manualRequirements:
          - "trustedExecution"
          - "authenticationSecrets"
          - "approvedTestIdentity"
        """;

    private static Dictionary<string, object?> Compact(Dictionary<string, object?> map) =>
        map.Where(pair => pair.Value is not null && !(pair.Value is string s && s.Length == 0) && !(pair.Value is Dictionary<string, object?> { Count: 0 }))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static string Slug(string value) => new([.. value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);

    /// <summary>A minimal YAML emitter for export (maps, lists, quoted scalars) that the profile parser reads back.</summary>
    private static string Yaml(object? value, int indent)
    {
        var builder = new StringBuilder();
        void Write(object? node, int level)
        {
            var pad = new string(' ', level * 2);
            switch (node)
            {
                case Dictionary<string, object?> map:
                    foreach (var (key, child) in map)
                    {
                        if (child is Dictionary<string, object?> or List<object?>) { builder.Append(pad).Append(key).Append(":\n"); Write(child, level + 1); }
                        else builder.Append(pad).Append(key).Append(": ").Append(Scalar(child)).Append('\n');
                    }
                    break;
                case List<object?> list:
                    foreach (var item in list)
                    {
                        if (item is Dictionary<string, object?> entry)
                        {
                            var first = true;
                            foreach (var (key, child) in entry) { builder.Append(pad).Append(first ? "- " : "  ").Append(key).Append(": ").Append(Scalar(child)).Append('\n'); first = false; }
                        }
                        else builder.Append(pad).Append("- ").Append(Scalar(item)).Append('\n');
                    }
                    break;
            }
        }
        Write(value, indent);
        return builder.ToString();
    }

    private static string Scalar(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "\"" + value.ToString()!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
    };
}
