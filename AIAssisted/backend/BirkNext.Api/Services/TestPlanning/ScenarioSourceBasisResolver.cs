using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Specifications;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The one consistent source basis a discovery reads (2026-11-23). Reuses the shared analysis-baseline rule (<see cref="SourceBaselineService"/>): every
/// source bound to the project is mapped to its logical source (repository identity), and each logical source contributes exactly the version every
/// other module analyses — the selected baseline, else its only (or content-identical) version. Different versions without a selected baseline are
/// never guessed between: that source is unresolved and the basis is incomplete. Importing a newer version adds it, it does not remove the other
/// sources of the basis (contracts, specifications …). A deliberate look at another version (<c>inspect</c>) is a Historical basis that never becomes
/// the project's current one. The specification comes from the import that produced the resolved snapshot (server-held), not from the browser.
/// </summary>
public sealed class ScenarioSourceBasisResolver(SourceBaselineService baselines, IqrSourceStore store, SpecificationCatalogService? catalogs = null)
{
    public const string Inspected = "INSPECTED";
    private IReadOnlyList<IqrSourceSnapshot>? _identities;
    private IReadOnlyDictionary<string, Guid>? _decided;

    /// <summary>The resolved basis, the full snapshots of its resolved members (primary first), the specification evidence to read and plain-language notes.</summary>
    public sealed record Resolution(DiscoverySourceBasis Basis, IReadOnlyList<IqrSourceSnapshot> Snapshots, IReadOnlyList<CoverageWorkspaceEvidence> Specifications, List<string> Limitations)
    {
        public IqrSourceSnapshot? Primary => Snapshots.Count > 0 ? Snapshots[0] : null;
    }

    public async Task<Resolution> ResolveAsync(string projectKey, Guid? snapshotId, IReadOnlyCollection<Guid> memberIds, bool inspect, CoverageWorkspaceEvidence? browser,
        bool useWorkingSpecification, CancellationToken ct)
    {
        var limitations = new List<string>();
        var problems = new List<string>();
        var identities = await baselines.SnapshotsAsync(ct);
        var requested = memberIds.Concat(snapshotId is { } s ? [s] : []).Distinct().ToList();
        var found = new List<IqrSourceSnapshot>();
        foreach (var id in requested)
        {
            var snapshot = identities.FirstOrDefault(x => x.Id == id) ?? await store.FindSourceAnalysisAsync(id, ct);
            if (snapshot is null) { problems.Add("En kildeversjon som er knyttet til prosjektet finnes ikke lenger (fjernet eller nullstilt)."); continue; }
            found.Add(snapshot);
        }
        var primaryKey = snapshotId is { } p && found.FirstOrDefault(x => x.Id == p) is { } ps ? SourceBaselineService.KeyOf(ps)
            : found.Select(SourceBaselineService.KeyOf).FirstOrDefault();
        var keys = found.Select(SourceBaselineService.KeyOf).Distinct(StringComparer.Ordinal).OrderBy(k => k == primaryKey ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();
        if (inspect && snapshotId is { } inspectedId)
        {
            var inspected = found.FirstOrDefault(x => x.Id == inspectedId) ?? throw new TestPlanException(404, "snapshot-not-found", "Den valgte kildeversjonen finnes ikke.");
            // Another application's (or branch's) source is never mixed into this project's basis: only another version of one of its own sources.
            var own = found.Where(x => memberIds.Contains(x.Id)).Select(SourceBaselineService.KeyOf).ToHashSet(StringComparer.Ordinal);
            if (own.Count > 0 && !own.Contains(SourceBaselineService.KeyOf(inspected)))
                throw new TestPlanException(400, "snapshot-not-in-project", "Den valgte kildeversjonen hører ikke til dette prosjektets kilder. Velg en versjon av prosjektets egne kilder.");
        }

        var decided = await baselines.CurrentBaselinesAsync(ct);
        var members = new List<DiscoverySourceMember>();
        var resolvedIds = new List<Guid>();
        var historical = false;
        foreach (var key in keys)
        {
            var versions = identities.Where(x => SourceBaselineService.KeyOf(x) == key).Concat(found.Where(x => SourceBaselineService.KeyOf(x) == key))
                .DistinctBy(x => x.Id).OrderByDescending(x => x.AnalyzedAt).ThenByDescending(x => x.Id).ToList();
            decided.TryGetValue(key, out var baselineId);
            var hasBaseline = decided.ContainsKey(key);
            var current = SourceBaselineService.Authoritative(versions, x => x.Id, SourceBaselineService.ContentOf, x => x.AnalyzedAt, hasBaseline ? baselineId : null);
            var state = hasBaseline ? (current is null ? "BASELINE_UNAVAILABLE" : "BASELINE_SELECTED")
                : versions.Count == 1 ? "SINGLE_SNAPSHOT" : current is not null ? "IDENTICAL_CONTENT" : "BASELINE_NOT_SELECTED";
            var identity = ReviewSourceEvidenceProvider.Identity(versions[0]);
            var used = current;
            if (inspect && snapshotId is { } want && versions.FirstOrDefault(x => x.Id == want) is { } chosen && chosen.Id != current?.Id)
            {
                used = chosen; state = Inspected; historical = true;
                limitations.Add($"Historisk innsyn: {identity.DisplayName} leses fra {chosen.Archive.FileName} ({chosen.AnalyzedAt:yyyy-MM-dd HH:mm}), ikke fra gjeldende kildeversjon. Resultatet endrer ikke gjeldende testplaner.");
            }
            else if (!inspect && snapshotId is { } linked && versions.Any(x => x.Id == linked) && current is not null && current.Id != linked)
                limitations.Add($"{identity.DisplayName}: prosjektets import er ikke gjeldende kildeversjon; {current.Archive.FileName} brukes (analysegrunnlaget).");
            if (used is null)
                problems.Add(state == "BASELINE_UNAVAILABLE"
                    ? $"{identity.DisplayName}: valgt analysegrunnlag finnes ikke lenger. Velg kildeversjon på nytt under Kildeversjoner."
                    : $"{identity.DisplayName}: {versions.Select(SourceBaselineService.ContentOf).Distinct(StringComparer.Ordinal).Count()} ulike versjoner er importert og ingen er valgt som analysegrunnlag. Velg kildeversjon under Kildeversjoner.");
            var newer = versions.FirstOrDefault(x => used is not null && x.AnalyzedAt > used.AnalyzedAt && SourceBaselineService.ContentOf(x) != SourceBaselineService.ContentOf(used))
                ?? (used is null ? SourceBaselineService.Preview(versions) : null);
            members.Add(new DiscoverySourceMember
            {
                RepositoryKey = key, DisplayName = identity.DisplayName, SnapshotId = used?.Id, ArchiveName = used?.Archive.FileName, AnalyzedAt = used?.AnalyzedAt,
                ContentFingerprint = used is null ? null : SourceBaselineService.ContentOf(used), StateCode = state, NewerSnapshotId = newer?.Id, NewerArchiveName = newer?.Archive.FileName,
            });
            if (used is not null) resolvedIds.Add(used.Id);
        }

        var full = new List<IqrSourceSnapshot>();
        foreach (var id in resolvedIds)
            if (await store.FindSourceAnalysisAsync(id, ct) is { } snapshot) full.Add(snapshot);

        // The specification: the imported one that belongs to the resolved source (same import), else the project's own imported specification.
        var specifications = new List<CoverageWorkspaceEvidence>();
        var origin = SpecificationOrigin.None;
        string? reference = null, specFingerprint = null;
        (BirkNext.Specifications.SpecificationCatalogView Catalog, List<StoredSpecificationEvidence> Evidence)? imported = null;
        if (catalogs is not null && !useWorkingSpecification)
        {
            foreach (var snapshot in full)
                if ((imported = await catalogs.EvidenceAsync(snapshot.Id, null, ct, snapshot.ProjectImport?.ImportId)) is not null) break;
            if (imported is null && resolvedIds.Count == 0) imported = await catalogs.EvidenceAsync(null, projectKey, ct);
        }
        if (useWorkingSpecification && browser is not null)
        {
            origin = SpecificationOrigin.WorkingDraft;
            specifications.Add(browser);
            specFingerprint = SpecificationEvidenceBuilder.SemanticFingerprint(browser);
            reference = "working:" + specFingerprint[..16];
            limitations.Add("Utkast: scenarioene fra spesifikasjonen er lest fra arbeidsteksten i nettleseren, ikke fra den importerte kildeversjonen.");
        }
        else if (imported is { } held)
        {
            origin = SpecificationOrigin.ImportedSnapshot;
            reference = $"{held.Catalog.ProjectKey}#r{held.Catalog.Revision}";
            var selected = browser?.SpecificationArtifacts.FirstOrDefault()?.Path is { } path
                ? held.Evidence.Where(e => BirkNext.Specifications.SpecificationCatalogRules.SamePath(e.Path, path)).ToList() : [];
            var chosen = selected.Count > 0 ? selected : held.Evidence;
            // The browser's module scope keeps candidate identities as the browser had them; several files without a selection keep their own path.
            foreach (var e in chosen)
                specifications.Add(e.Evidence with
                {
                    ArtifactScope = selected.Count > 0 ? browser!.ArtifactScope : chosen.Count > 1 ? e.Path : null,
                    ProjectName = browser?.ProjectName,
                    Executions = browser?.Executions ?? [],
                    // A clarification resolved in BirkNext is recorded in the browser's lifecycle: keep those states when the questions are the same.
                    Clarifications = MergeClarifications(e.Evidence.Clarifications, browser?.Clarifications),
                });
            specFingerprint = E.Sha(string.Join("\n", specifications.Select(SpecificationEvidenceBuilder.SemanticFingerprint)));
            if (browser is { AcceptanceScenarios.Count: > 0 } && selected.Count == 1
                && SpecificationEvidenceBuilder.SemanticFingerprint(browser) != SpecificationEvidenceBuilder.SemanticFingerprint(selected[0].Evidence))
                limitations.Add("Spesifikasjonen i nettleseren er en annen versjon enn den importerte. Den importerte spesifikasjonen er brukt; velg \"Bruk arbeidsutkast\" for å lese nettleserens tekst som utkast.");
        }
        else if (browser is { } unverified && (unverified.SpecificationAvailable || unverified.AcceptanceScenarios.Count > 0 || unverified.Requirements.Count > 0))
        {
            origin = SpecificationOrigin.BrowserUnverified;
            specifications.Add(unverified);
            specFingerprint = SpecificationEvidenceBuilder.SemanticFingerprint(unverified);
            reference = "browser:" + specFingerprint[..16];
            limitations.Add("Den importerte spesifikasjonen er ikke lagret på serveren for denne kildeversjonen (eldre import). Nettleserens kopi er brukt og kan ikke kontrolleres mot kildeversjonen; importer prosjektet på nytt for å få sporbarhet.");
        }

        var kind = historical ? DiscoveryBasisKind.Historical : members.Count == 0 ? DiscoveryBasisKind.DocumentsOnly : DiscoveryBasisKind.Current;
        var application = primaryKey ?? projectKey;
        var fingerprint = E.Sha(JsonSerializer.Serialize(new
        {
            kind, application, Members = members.Select(m => new { m.RepositoryKey, m.SnapshotId, m.ContentFingerprint, m.StateCode }), origin, specFingerprint,
        }, E.Json))[..24];
        var basis = new DiscoverySourceBasis
        {
            ApplicationKey = application, Kind = kind, Members = members, SpecificationOrigin = origin, SpecificationReference = reference, SpecificationFingerprint = specFingerprint,
            Fingerprint = fingerprint, Problems = [.. problems.Distinct(StringComparer.Ordinal)],
        };
        // Primary first: the source the project was opened for (its application source).
        var ordered = full.OrderBy(x => SourceBaselineService.KeyOf(x) == primaryKey ? 0 : 1).ToList();
        return new Resolution(basis, ordered, specifications, limitations);
    }

    /// <summary>
    /// Live freshness of a basis without discovering again: per logical source, a version imported after the one the basis used with different
    /// content ("Ny kildeversjon"), or a changed baseline decision. Identity-only reads; an identical re-import is not news.
    /// </summary>
    public async Task<List<DiscoverySourceMember>> NewerSourcesAsync(DiscoverySourceBasis basis, CancellationToken ct)
    {
        if (basis.Members.Count == 0) return [];
        // Read once per request (the resolver is scoped): a workspace with many plans asks this for each of them.
        var identities = _identities ??= await baselines.SnapshotsAsync(ct);
        var decided = _decided ??= await baselines.CurrentBaselinesAsync(ct);
        var result = new List<DiscoverySourceMember>();
        foreach (var m in basis.Members)
        {
            var versions = identities.Where(x => SourceBaselineService.KeyOf(x) == m.RepositoryKey).OrderByDescending(x => x.AnalyzedAt).ThenByDescending(x => x.Id).ToList();
            var newer = versions.FirstOrDefault(x => (m.AnalyzedAt is null || x.AnalyzedAt > m.AnalyzedAt) && SourceBaselineService.ContentOf(x) != m.ContentFingerprint);
            var baselineMoved = decided.TryGetValue(m.RepositoryKey, out var selected) && m.SnapshotId is { } used && selected != used && m.StateCode != Inspected;
            if (newer is null && !baselineMoved) continue;
            var target = newer ?? versions.FirstOrDefault(x => x.Id == selected);
            result.Add(m with { NewerSnapshotId = target?.Id, NewerArchiveName = target is null ? null : $"{target.Archive.FileName} ({target.AnalyzedAt:yyyy-MM-dd HH:mm})" });
        }
        return result;
    }

    private static List<CoverageClarification> MergeClarifications(List<CoverageClarification> imported, List<CoverageClarification>? browser)
    {
        if (browser is null || browser.Count == 0) return imported;
        var recorded = browser.GroupBy(c => c.Question.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return [.. imported.Select(c => recorded.TryGetValue(c.Question.Trim(), out var b) && b.State == CoverageClarification.Resolved ? c with { State = b.State, Resolution = b.Resolution } : c)];
    }
}
