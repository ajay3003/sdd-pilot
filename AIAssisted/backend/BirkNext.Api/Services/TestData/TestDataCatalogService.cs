using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// The catalog as the page and consumers see it: the safe summaries from every source, plus what Test Data itself owns — lifecycle (explicit
/// approval of a version, deprecation), registration from source candidates, contract drift, inbound consumer requirements and explicit
/// usage references. Consumers are generic records (type + stable id + display name); there is no consumer-specific logic here. Nothing here
/// returns a value: approval, matching and binding work on ids, fingerprints and metadata.
/// </summary>
public sealed class TestDataCatalogService(TestDataSchemaValidationService validation, TestDataCatalog catalog, TestDataRegistryStore registry,
    TestDataCandidateService candidates, IReviewSourceEvidenceProvider sources, TimeProvider time, TestDataLifecycleService? lifecycle = null,
    IEnumerable<ITestDataConsumerDeclarations>? consumers = null, CanonicalFixtureStore? canonicalFixtures = null, IEnumerable<ITestDataCanonicalFixtures>? canonical = null)
{
    /// <summary>The page view: summaries enriched with consumer counts and contract status (drift against the latest snapshot of the repository).</summary>
    public async Task<TestDataCatalogView> ViewAsync(CancellationToken ct)
    {
        // What declared consumers consume is persisted before usage is counted (requirements once; deterministic bindings by stable asset id).
        if (consumers is not null) TestDataConsumerBindings.Ensure(registry, catalog, consumers, time.GetUtcNow());
        var view = await validation.ViewAsync(ct);
        var usages = registry.Usages();
        // "Used by N": every active consumer reference (performance scenarios, Critical E2E flows, Connected System bindings by asset id, explicit
        // bindings) — one per consumer, however many requirements of that consumer resolve the asset. Suggestions are never references.
        var all = lifecycle is null ? null : await lifecycle.UsagesAsync(ct);
        var references = all?.Where(u => !u.Unresolved).ToLookup(u => u.AssetId ?? u.Alias, StringComparer.Ordinal);
        var registered = registry.Assets().ToDictionary(a => a.Id, StringComparer.Ordinal);
        var drift = await DriftAsync(registered.Values, ct);
        return view with
        {
            Assets = [.. view.Assets.Select(asset =>
            {
                var (contract, note) = ContractStatus(asset, registered.GetValueOrDefault(asset.Id), drift);
                var status = contract == TestDataContractStatus.NeedsReview && asset.Status == TestDataAssetStatus.Approved ? TestDataAssetStatus.NeedsReview : asset.Status;
                var consumers = references is not null
                    ? references[asset.Id].Concat(references[asset.Alias]).Select(u => $"{u.ConsumerType}|{u.ConsumerId}").Distinct(StringComparer.Ordinal).Count()
                    : usages.Where(u => u.AssetId == asset.Id).Select(u => u.Consumer.Key).Distinct().Count();
                // Inputs the payload was generated from (mapped Test Data values) changed since: regeneration recommended, never a silent rewrite.
                var changed = TestDataValueVersions.Changed(asset, view.Assets);
                var staleness = changed.Count == 0 ? null
                    : $"Regeneration recommended: {string.Join(", ", changed.Select(c => c.Missing ? $"{c.Alias} (no longer in Test Data)" : c.Alias))} changed since this version was generated.";
                return asset with { Consumers = consumers, ContractStatus = contract, ContractNote = note, Status = status, Staleness = staleness };
            })],
        };
    }

    /// <summary>Per registered candidate-derived asset: the same candidate re-derived from the latest snapshot of its repository (null = gone).</summary>
    private async Task<Dictionary<string, (Guid Latest, TestDataCandidate? Candidate)>> DriftAsync(IEnumerable<RegisteredTestDataAsset> assets, CancellationToken ct)
    {
        var result = new Dictionary<string, (Guid, TestDataCandidate?)>(StringComparer.Ordinal);
        var lineage = assets.Where(a => a.CandidateId is not null && a.SnapshotId is not null && a.RepositoryKey is not null).ToList();
        if (lineage.Count == 0) return result;
        var snapshots = await sources.ListAsync("", ct);
        var perSnapshot = new Dictionary<Guid, IReadOnlyList<TestDataCandidate>>();
        foreach (var asset in lineage)
        {
            var latest = snapshots.Where(s => ReviewSourceEvidenceProvider.Identity(s).Key == asset.RepositoryKey).MaxBy(s => s.AnalyzedAt);
            if (latest is null || latest.Id == asset.SnapshotId) continue;
            if (!perSnapshot.TryGetValue(latest.Id, out var list)) perSnapshot[latest.Id] = list = await candidates.CandidatesAsync("", latest.Id, ct) ?? [];
            result[asset.Id] = (latest.Id, list.FirstOrDefault(c => c.Id == asset.CandidateId));
        }
        return result;
    }

    private static (TestDataContractStatus, string?) ContractStatus(TestDataAssetSummary asset, RegisteredTestDataAsset? registered,
        IReadOnlyDictionary<string, (Guid Latest, TestDataCandidate? Candidate)> drift)
    {
        if (registered is { CandidateId: not null })
        {
            if (!drift.TryGetValue(asset.Id, out var newer)) return (TestDataContractStatus.Valid, "Derived from the latest analyzed snapshot of its repository.");
            if (newer.Candidate is null) return (TestDataContractStatus.NeedsReview, $"Contract no longer present in the latest snapshot ({newer.Latest:D}) · review required.");
            return newer.Candidate.StructureFingerprint == registered.StructureFingerprint
                ? (TestDataContractStatus.Compatible, "The latest snapshot declares the same structure.")
                : (TestDataContractStatus.NeedsReview, $"Contract changed · review required (latest snapshot {newer.Latest:D}). The asset is not modified.");
        }
        if (asset.SchemaCheck is { } check)
            return check.Status switch
            {
                TestDataSchemaStatus.Validated => (TestDataContractStatus.Valid, check.Detail),
                TestDataSchemaStatus.Invalid => (TestDataContractStatus.Invalid, check.Detail),
                TestDataSchemaStatus.NotEvaluated => (TestDataContractStatus.NotAssessed, check.Detail),
                _ => (TestDataContractStatus.NeedsReview, check.Detail),
            };
        return asset.SchemaRef is { Length: > 0 } || asset.Schema is not null ? (TestDataContractStatus.NotAssessed, "A contract is referenced; no structural check applies to this source.")
            : (TestDataContractStatus.NotApplicable, null);
    }

    public async Task<TestDataConsumerView> ConsumersAsync(CancellationToken ct)
    {
        var view = await ViewAsync(ct);
        var assets = view.Assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var usages = registry.Usages().Select(u =>
        {
            var (status, reason) = TestDataCatalogRules.UsageStatus(u, assets.GetValueOrDefault(u.AssetId));
            return u with { Status = status, StatusReason = reason };
        }).OrderBy(u => u.Consumer.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var requirements = registry.Requirements().Select(r => r with
        {
            Status = TestDataCatalogRules.RequirementStatus(r, usages),
            BoundAssetId = usages.Where(u => u.RequirementId == r.Id).Select(u => u.AssetId).FirstOrDefault(),
        }).OrderBy(r => r.Status).ThenBy(r => r.Consumer.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var required = requirements.Where(r => r.Required).ToList();
        var overview = new TestDataOverview(
            view.Assets.Count(a => a.Status == TestDataAssetStatus.Approved), view.Assets.Count(a => a.Status == TestDataAssetStatus.NeedsReview),
            view.Assets.Count(a => a.Status == TestDataAssetStatus.Draft), view.Assets.Count(a => a.Status == TestDataAssetStatus.Deprecated),
            requirements.Count(r => r.Status != TestDataRequirementStatus.Resolved), required.Count(r => r.Status == TestDataRequirementStatus.Resolved), required.Count,
            requirements.Count(r => r.EnvironmentId is { } env && TestDataCatalogRules.Match(r, view.Assets).Any(m => m.State == TestDataMatchState.Conflict && m.Reason.StartsWith("Scoped to", StringComparison.Ordinal))
                && !TestDataCatalogRules.Match(r, view.Assets).Any(m => m.State is TestDataMatchState.Exact or TestDataMatchState.Compatible or TestDataMatchState.Possible)),
            view.Assets.Count(a => a.ContractStatus is TestDataContractStatus.NeedsReview or TestDataContractStatus.Invalid));
        return new(requirements, usages, overview);
    }

    /// <summary>
    /// Registers asset metadata. A candidate is re-derived from its exact snapshot (never the newest one; the client never supplies structure);
    /// the same candidate of the same repository, or an alias in use, is never registered twice — the existing asset is returned instead.
    /// The registered asset is not approved.
    /// </summary>
    public async Task<(RegisteredTestDataAsset? Asset, string? Existing, string? Error)> RegisterAsync(TestDataRegistrationRequest request, CancellationToken ct)
    {
        var alias = request.Alias.Trim();
        if (!TestDataCatalogRules.AliasPattern.IsMatch(alias)) return (null, null, "The alias must be lowercase letters, digits and hyphens (at most 64).");
        if (catalog.Assets().Any(a => a.Alias == alias && a.SourceId != RegisteredTestDataProvider.ProviderId)) return (null, catalog.Assets().First(a => a.Alias == alias).Id, $"The alias {alias} is already used by another source.");
        var asset = new RegisteredTestDataAsset
        {
            Alias = alias, Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(), DataType = request.DataType,
            EnvironmentId = string.IsNullOrWhiteSpace(request.EnvironmentId) ? null : request.EnvironmentId.Trim(), RegisteredAt = time.GetUtcNow(),
            Tags = [.. request.Tags.Select(t => t.Trim()).Where(t => t.Length is > 0 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(20)],
            SemanticRole = TestDataLifecycleService.Role(request.SemanticRole), AppliesTo = TestDataLifecycleService.Contexts(request.AppliesTo),
        };
        if (request.SnapshotId is { } snapshotId && request.CandidateId is { Length: > 0 } candidateId)
        {
            if (await sources.ResolveAsync("", snapshotId, ct) is not { } snapshot) return (null, null, "The source snapshot is unavailable; nothing is substituted.");
            var candidate = (await candidates.CandidatesAsync("", snapshotId, ct) ?? []).FirstOrDefault(c => c.Id == candidateId);
            if (candidate is null) return (null, null, "The candidate is not present in that snapshot.");
            asset = asset with
            {
                Fields = [.. candidate.Structure], SchemaRef = candidate.SchemaRef, SnapshotId = snapshot.Id, RepositoryKey = ReviewSourceEvidenceProvider.Identity(snapshot).Key,
                ArchiveName = snapshot.Archive.FileName, CandidateId = candidate.Id, CandidateReason = candidate.Reason, StructureFingerprint = candidate.StructureFingerprint,
                Origin = "Source Analysis candidate",
            };
        }
        else
        {
            // Manual registration: names (and declared types) only — anything after "=" could be a value and is dropped.
            asset = asset with { Fields = [.. request.Fields.Select(f => f.Split('=')[0].Trim()).Where(f => f.Length is > 0 and <= 120).Distinct(StringComparer.Ordinal).Take(100)] };
        }
        var (stored, existing) = registry.Register(asset);
        return stored is null ? (null, existing, "This candidate or alias is already a Test Data asset.") : (stored, null, null);
    }

    /// <summary>
    /// Does an observed runtime value equal the value of an approved Test Data asset (same data type, usable in the environment)? Compared here,
    /// in the backend; only the alias is returned. Nothing is stored, approved or created.
    /// </summary>
    public ObservedValueMatchResult MatchObservedValue(ObservedValueMatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Value)) return new(false, null, "No observed value to compare.");
        var match = catalog.Assets()
            .Where(a => a.Approval == TestDataApproval.Approved && a.Value is not null && TestDataDataTypes.Of(a.Kind, a.MediaType) == request.DataType
                && (a.Environment is null || request.EnvironmentId is null || string.Equals(a.Environment, request.EnvironmentId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => a.Alias, StringComparer.Ordinal)
            .FirstOrDefault(a => a.Value!.Is(request.Value));
        return match is null
            ? new(false, null, "No approved Test Data asset holds this value. You can register the parameter as a Test Data candidate for review.")
            : new(true, match.Alias, $"Existing approved Test Data match found: {match.Alias}.");
    }

    /// <summary>
    /// Explicit approval of one version (default: the current one) — the approve action of the Test Data API. A positive XML payload is approved
    /// only when it is valid against its exact declared XSD: Invalid is refused; when the backend cannot evaluate the schema, the validation
    /// recorded at generation against that same XSD must be Valid. A deliberately invalid negative-test fixture is approved as such. Approving a
    /// draft version makes it the current version of the same asset id, so every consumer bound to the id uses it without rebinding.
    /// </summary>
    public async Task<string?> ApproveAsync(string assetId, int? version, CancellationToken ct)
    {
        if (catalog.Assets().FirstOrDefault(a => a.Id == assetId || a.AssetId == assetId) is not { } asset) return "Unknown asset.";
        if (catalog.VersionOf(asset, version) is not { } target) return $"{asset.Alias} has no version {version}.";
        // Every approval attempt of an XML version leaves server-side validation evidence (trust level, exact schema identity), never the value.
        if (target.Schema is not null) await validation.ValidateAndRecordAsync(target with { AssetId = asset.AssetId }, TimeProvider.System, ct);
        var negative = target.Capabilities.Contains(TestDataProtection.NegativeTest, StringComparer.Ordinal);
        if (!negative && target.Schema is not null && TestDataValidator.MediaType(target.MediaType).EndsWith("xml", StringComparison.Ordinal))
        {
            var check = await validation.CheckAsync(target, ct);
            if (check?.Status == TestDataSchemaStatus.Invalid)
                return $"Validation failed: {target.Alias} (version {target.Version ?? 1}) is invalid against {check.Schema}; invalid XML cannot be approved. "
                    + string.Join(" ", check.Issues.Take(3));
            if (check?.Status != TestDataSchemaStatus.Validated && target.RecordedValidation is { } recorded && recorded != "Valid")
                return $"{target.Alias} (version {target.Version ?? 1}) did not pass validation against its XSD when it was generated; invalid XML cannot be approved.";
            // Schema-backed XML is approved only on the server's own validation. A browser-reported "Valid" is client-reported only: the asset can
            // still be saved and kept as a draft (Needs validation), but not approved until the XSD is available to the server and validates it.
            if (check?.Status != TestDataSchemaStatus.Validated)
                return ServerValidationRequired(target, check);
        }
        return Approve(asset, target);
    }

    public const string ServerValidationRequiredTitle = "Approval blocked — server validation required";

    private static string ServerValidationRequired(TestDataAsset target, TestDataSchemaCheck? check) =>
        $"{ServerValidationRequiredTitle}. {target.Alias} (version {target.Version ?? 1}) is "
        + (target.RecordedValidation == "Valid" ? "only client-reported as valid. " : "not validated by the server. ")
        + (check?.Status is TestDataSchemaStatus.SchemaUnavailable or TestDataSchemaStatus.DependencyUnresolved or null
            ? $"The associated XSD ({target.Schema!.SchemaPath}) is not available to the server. "
            : $"{check.Detail} ")
        + "Associate the XSD (import a source package that contains it, or POST api/test-data/schema-associations), retry validation, or keep it as a draft.";

    /// <summary>Explicit approval of the asset's current version (fingerprint). A deprecated asset is not approved for new usage.</summary>
    public string? Approve(string assetId) =>
        catalog.Assets().FirstOrDefault(a => a.Id == assetId || a.AssetId == assetId) is { } asset ? Approve(asset, asset) : "Unknown asset.";

    private string? Approve(TestDataAsset asset, TestDataAsset version)
    {
        if (asset.Deprecated) return "A deprecated asset is not approved for new usage.";
        var current = registry.Lifecycle(asset.Id) ?? new TestDataLifecycleRecord { AssetId = asset.Id };
        return registry.SetLifecycle(current with { ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(version), ApprovedAt = time.GetUtcNow() })
            ? null : "The approval could not be stored (the Test Data registry file could not be written); nothing was approved. Try again.";
    }

    /// <summary>Deprecation keeps the asset and every reference to it; it is no longer offered for new usage.</summary>
    public string? Deprecate(string assetId)
    {
        if (catalog.Assets().All(a => a.Id != assetId)) return "Unknown asset.";
        var current = registry.Lifecycle(assetId) ?? new TestDataLifecycleRecord { AssetId = assetId };
        registry.SetLifecycle(current with { Deprecated = true, DeprecatedAt = time.GetUtcNow() });
        return null;
    }

    public (ConsumerTestDataRequirement? Requirement, string? Error) AddRequirement(ConsumerTestDataRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.Consumer.ConsumerType) || string.IsNullOrWhiteSpace(requirement.Consumer.ConsumerId))
            return (null, "A requirement names its consumer (type and stable id).");
        var stored = registry.AddRequirement(requirement with
        {
            Id = string.IsNullOrWhiteSpace(requirement.Id) ? $"req-{Guid.NewGuid():N}"[..16] : requirement.Id,
            Consumer = requirement.Consumer with { DisplayName = string.IsNullOrWhiteSpace(requirement.Consumer.DisplayName) ? requirement.Consumer.ConsumerId : requirement.Consumer.DisplayName },
            Usage = string.IsNullOrWhiteSpace(requirement.Usage) ? "Unspecified" : requirement.Usage.Trim(),
            RequestedFields = [.. requirement.RequestedFields.Select(f => f.Trim()).Where(f => f.Length > 0).Take(30)],
            RegisteredAt = time.GetUtcNow(), Status = TestDataRequirementStatus.Missing, BoundAssetId = null,
        });
        return (stored, null);
    }

    public bool RemoveRequirement(string id) => registry.RemoveRequirement(id);

    /// <summary>Suggestions for a requirement (registered or ad hoc from a consumer deep link). Side-effect free: nothing is bound.</summary>
    public async Task<List<TestDataMatch>> MatchesAsync(ConsumerTestDataRequirement requirement, bool includeDeprecated, CancellationToken ct) =>
        TestDataCatalogRules.Match(requirement, (await ViewAsync(ct)).Assets, includeDeprecated);

    /// <summary>
    /// An explicit binding, made by the person or consumer: the usage records the asset id and the version it was bound to. Only an approved,
    /// non-deprecated asset in the requirement's scope can be bound; the value is never copied.
    /// </summary>
    public async Task<(TestDataUsageReference? Usage, string? Error)> BindAsync(TestDataUsageReference usage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(usage.Consumer.ConsumerType) || string.IsNullOrWhiteSpace(usage.Consumer.ConsumerId)) return (null, "A usage names its consumer (type and stable id).");
        var assets = (await ViewAsync(ct)).Assets;
        if (assets.FirstOrDefault(a => a.Id == usage.AssetId) is not { } asset) return (null, "Unknown asset.");
        if (asset.Status != TestDataAssetStatus.Approved) return (null, $"{asset.Alias} is {TestDataCatalogRules.Label(asset.Status ?? TestDataAssetStatus.Draft).ToLowerInvariant()}; only approved assets are bound.");
        var requirement = usage.RequirementId is { } rid ? registry.Requirements().FirstOrDefault(r => r.Id == rid) : null;
        if (usage.RequirementId is not null && requirement is null) return (null, "Unknown requirement.");
        var environment = usage.EnvironmentId ?? requirement?.EnvironmentId;
        if (!TestDataCatalogRules.InScope(asset, environment)) return (null, $"{asset.Alias} is scoped to {asset.Environment}, not {environment}.");
        var stored = registry.Bind(usage with
        {
            Id = $"use-{Guid.NewGuid():N}"[..16], AssetFingerprint = TestDataCatalogRules.Version(asset), EnvironmentId = environment,
            Consumer = requirement?.Consumer ?? usage.Consumer, Usage = string.IsNullOrWhiteSpace(usage.Usage) ? requirement?.Usage ?? "Unspecified" : usage.Usage.Trim(),
            BoundAt = time.GetUtcNow(), Status = TestDataUsageStatus.Resolved, StatusReason = null,
        });
        return (stored, null);
    }

    public bool Unbind(string id) => registry.Unbind(id);

    /// <summary>
    /// Stores a validated, positive XML fixture under its canonical alias in the backend canonical fixture store, as the next (unapproved) version
    /// of that one asset. Approval stays the separate Test Data decision; an approved version is never rewritten.
    /// </summary>
    public (int? Version, string? Error) SaveCanonicalFixture(System.Text.Json.Nodes.JsonObject item)
    {
        if (canonicalFixtures is null) return (null, "Canonical fixtures cannot be stored in this host.");
        return canonicalFixtures.Add(item, [.. (canonical ?? []).SelectMany(c => c.Fixtures)], time.GetUtcNow());
    }
}
