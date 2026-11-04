using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>
/// Source + Functional scope for Delivery (Overview first, Reports and Stories through the same projection). Membership is resolved here from
/// stable ids — the snapshot's source identity, canonical Feature —Implements→ Capability relations, Functional Quality Scope definitions —
/// and the frozen report is projected onto it (<see cref="DeliveryReportScoping"/>), so every number on the page comes from the scoped rows.
/// No capability is inferred here: change intents and capabilities come from the canonical engine, domain flows and saved scopes from FQS.
/// </summary>
public sealed class DeliveryScopeService(CanonicalInterpretationService? canonical = null, FunctionalQualityScopeService? functionalScopes = null)
{
    public const string SourceUnavailable = "Selected source is not available in this comparison.";
    public const string ScopeUnavailable = "Selected functional scope is not available in this snapshot.";

    private sealed record Side(DeliverySourceIdentity Source, IReadOnlyList<(string Module, string FeatureKey, string Title)> Features);

    public const string TechnicalIntentRule = "fq.technical.change-intent";

    /// <summary>A frozen report: current side = its current features; baseline side = the baseline's features (retained and removed).</summary>
    public async Task<DeliveryScopeView> ForReportAsync(Guid projectId, WeeklyDeliveryReport report, CanonicalScopeFilter filter, IReadOnlyList<Guid> projectSnapshots, CancellationToken ct,
        DeliveryWorkFilter? work = null)
    {
        var current = new Side(DeliverySourceIdentity.Of(report.Current), [.. report.Stories.Where(s => !s.RemovedFeature).Select(s => (s.Module, s.FeatureKey, s.Title))]);
        var baseline = new Side(DeliverySourceIdentity.Of(report.Baseline), [.. report.Stories.Where(s => !s.NewFeature).Select(s => (s.Module, s.FeatureKey, s.Title))]);
        var stories = report.Stories.GroupBy(s => s.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.SelectMany(s => s.Hierarchy?.Stories ?? []).Select(x => x.Title)], StringComparer.Ordinal);
        var view = await ResolveAsync(projectId, current, baseline, filter, [report.Current.SnapshotId, report.Baseline.SnapshotId, .. projectSnapshots], ct, work ?? new(), stories);
        if (view.Scope is null) return view with { Report = report };
        return view with { Report = DeliveryReportScoping.Project(report, view.Scope.CurrentFeatureKeys.ToHashSet(StringComparer.Ordinal), view.Scope.BaselineFeatureKeys.ToHashSet(StringComparer.Ordinal), view.Scope) };
    }

    /// <summary>A snapshot without a report: one side only (no baseline, so nothing is said about it).</summary>
    public async Task<DeliveryScopeView> ForSnapshotAsync(Guid projectId, DeliverySnapshotDetail detail, CanonicalScopeFilter filter, IReadOnlyList<Guid> projectSnapshots, CancellationToken ct,
        DeliveryWorkFilter? work = null)
    {
        var current = new Side(DeliverySourceIdentity.Of(detail.Summary), [.. detail.Features.Select(f => (f.Module, f.Key, f.Title))]);
        var view = await ResolveAsync(projectId, current, null, filter, [detail.Summary.Id, .. projectSnapshots], ct, work ?? new(), null);
        if (view.Scope is null) return view with { Snapshot = detail };
        var keys = view.Scope.CurrentFeatureKeys.ToHashSet(StringComparer.Ordinal);
        return view with { Snapshot = detail with { Features = [.. detail.Features.Where(f => keys.Contains(f.Key))] } };
    }

    private async Task<DeliveryScopeView> ResolveAsync(Guid projectId, Side current, Side? baseline, CanonicalScopeFilter filter, IReadOnlyList<Guid> snapshotOrder, CancellationToken ct,
        DeliveryWorkFilter work, IReadOnlyDictionary<string, IReadOnlyList<string>>? storyTitles)
    {
        // Every feature that can be in a scope: the current side and the baseline side (a removed feature only exists in the baseline).
        var all = current.Features.Concat(baseline?.Features ?? []).DistinctBy(f => f.FeatureKey).ToList();
        var scopes = new List<(DeliveryFunctionalScopeOption Option, Dictionary<string, string> Members, List<DeliveryScopeMember> Candidates)>();
        string? canonicalScope = null;
        string? note = null;

        if (canonical is not null)
        {
            try
            {
                canonicalScope = await canonical.ScopeOfDeliveryAsync([.. snapshotOrder.Distinct()], ct);
                if (canonicalScope is null)
                    note = "No canonical interpretation covers this Delivery project yet: capabilities and change intents appear after Correlation Review → Interpret with a Delivery snapshot.";
                else
                {
                    var identity = all.ToDictionary(f => CorrelationRules.FeatureIdentity(f.Module, f.FeatureKey), f => f, StringComparer.Ordinal);
                    foreach (var s in await canonical.FunctionalScopesAsync(canonicalScope, identity.Keys, ct))
                    {
                        var members = s.Members.Where(m => identity.ContainsKey(m.FeatureIdentity))
                            .ToDictionary(m => identity[m.FeatureIdentity].FeatureKey, m => $"{m.Path}. {m.Evidence}", StringComparer.Ordinal);
                        if (members.Count == 0) continue; // candidates only: a suggestion for review, not a scope
                        var candidates = s.Candidates.Where(m => identity.ContainsKey(m.FeatureIdentity))
                            .Select(m => new DeliveryScopeMember(identity[m.FeatureIdentity].FeatureKey, identity[m.FeatureIdentity].Title, $"{m.Path}. {m.Evidence}")).ToList();
                        scopes.Add((new(s.Id, s.Name, s.Kind, s.Detail, 0, 0, candidates.Count), members, candidates));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
            {
                note = "The canonical store is not available: capabilities and change intents cannot be listed.";
            }
        }

        if (functionalScopes is not null)
            foreach (var available in (await functionalScopes.AvailableAsync(projectId, ct)).Where(a => a.Kind != "Module"))
            {
                try
                {
                    var (definition, members) = await functionalScopes.FeatureMembershipAsync(projectId, available.Key, all, ct);
                    if (members.Count == 0) continue;
                    var kind = available.Key.StartsWith("custom:", StringComparison.Ordinal) ? FunctionalScopeKind.SavedScope : FunctionalScopeKind.DomainFlow;
                    scopes.Add((new(available.Key, definition.Name.Length > 0 ? definition.Name : available.Name, kind, available.Detail, 0, 0, 0), members, []));
                }
                catch (FunctionalQualityScopeException) { /* a scope deleted meanwhile: not offered */ }
            }

        // Options with dependent counts: a source counts the features of the selected functional scope, a scope counts the selected source's.
        var selectedScope = filter.FunctionalScopeId is { } sid ? scopes.FirstOrDefault(s => s.Option.Id == sid) : default;
        var invalid = new List<string>();
        if (filter.FunctionalScopeId is not null && selectedScope.Option is null) invalid.Add(ScopeUnavailable);
        var sourceIds = new[] { current.Source }.Concat(baseline is null ? [] : [baseline.Source]).DistinctBy(s => s.Id).ToList();
        if (filter.SourceId is not null && sourceIds.All(s => s.Id != filter.SourceId)) invalid.Add(SourceUnavailable);

        // Work type: the shared feature classification, plus canonical evidence — every member of a change intent most of whose members are
        // technical work is technical work too ("Translate the Tjeneste module to English" joins ADR-029 by review, not by its wording).
        var classes = all.ToDictionary(f => f.FeatureKey, f => FunctionalQualityClassifier.ClassifyFeature(f.Title, storyTitles?.GetValueOrDefault(f.FeatureKey)), StringComparer.Ordinal);
        foreach (var intent in scopes.Where(s => s.Option.Kind == FunctionalScopeKind.ChangeIntent))
            if (intent.Members.Keys.Count(k => classes.TryGetValue(k, out var c) && c.Category == QualityCategory.Technical) * 2 > intent.Members.Count)
                foreach (var key in intent.Members.Keys.Where(classes.ContainsKey)) classes[key] = (QualityCategory.Technical, TechnicalIntentRule);
        bool SourceScope(string key) => selectedScope.Option is null ? filter.FunctionalScopeId is null : selectedScope.Members.ContainsKey(key);
        bool InScope(string key) => SourceScope(key) && work.Includes(classes[key].Category);
        IEnumerable<string> SideKeys(Side? side, string? sourceId, Func<string, bool> scopeTest) =>
            side is null || sourceId is not null && side.Source.Id != sourceId ? [] : side.Features.Select(f => f.FeatureKey).Where(scopeTest);

        var sources = sourceIds.Select(s => new DeliveryScopeSourceOption(s.Id, s.Name, s.Provenance,
            SideKeys(current, s.Id, InScope).Count(), SideKeys(baseline, s.Id, InScope).Count())).ToList();
        var options = scopes.Select(s => s.Option with
        {
            CurrentFeatures = SideKeys(current, filter.SourceId, s.Members.ContainsKey).Count(),
            BaselineFeatures = SideKeys(baseline, filter.SourceId, s.Members.ContainsKey).Count(),
        }).OrderBy(o => o.Kind).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();

        List<string> selected = invalid.Count > 0 ? [] : [.. SideKeys(current, filter.SourceId, SourceScope)];
        var byCategory = selected.GroupBy(k => classes[k].Category).ToDictionary(g => g.Key, g => g.Count());
        var workSummary = new DeliveryWorkSummary(selected.Count, selected.Count(k => classes[k].Category != QualityCategory.Technical),
            selected.Count(k => classes[k].Category == QualityCategory.Technical), byCategory, selected.Count(k => work.Includes(classes[k].Category)));
        var view = new DeliveryScopeView
        {
            Filter = filter, Sources = sources, Scopes = options, InvalidFilters = invalid, CanonicalScope = canonicalScope, InterpretationNote = note,
            Work = work, WorkSummary = workSummary, Classifications = [.. selected.Select(k => new DeliveryFeatureWork(k, classes[k].Category, classes[k].Rule))],
        };
        if (!filter.IsScoped && !work.IsActive) return view;

        var applied = invalid.Count == 0;
        var currentKeys = applied ? SideKeys(current, filter.SourceId, InScope).ToList() : [];
        var baselineKeys = applied ? SideKeys(baseline, filter.SourceId, InScope).ToList() : [];
        var source = sourceIds.FirstOrDefault(s => s.Id == filter.SourceId);
        var sourceInBaseline = baseline is not null && (filter.SourceId is null || baseline.Source.Id == filter.SourceId);
        var scopeInBaseline = baseline is not null && (filter.FunctionalScopeId is null || baselineKeys.Count > 0);
        var notices = new List<string>();
        if (applied && baseline is not null)
        {
            if (!sourceInBaseline)
                notices.Add($"Source not present in baseline: the baseline was imported from {baseline.Source.Name}. Change this period cannot be compared within {source?.Name ?? "this source"}.");
            else if (!scopeInBaseline)
                notices.Add($"Functional scope introduced in the current snapshot: no baseline feature belongs to {selectedScope.Option?.Name}. Its features read as new, not as progress.");
        }
        var titles = all.ToDictionary(f => f.FeatureKey, f => f.Title, StringComparer.Ordinal);
        var memberRows = currentKeys.Select(k => new DeliveryScopeMember(k, titles[k],
            string.Join(" · ", new[]
            {
                filter.SourceId is null ? null : $"Read from source {source?.Name}",
                selectedScope.Members?.GetValueOrDefault(k),
            }.Where(x => x is not null)))).ToList();
        var scope = new DeliveryReportScope
        {
            SourceId = filter.SourceId, SourceName = source?.Name, FunctionalScopeId = filter.FunctionalScopeId, FunctionalScopeName = selectedScope.Option?.Name,
            FunctionalScopeKind = selectedScope.Option?.Kind, CanonicalScope = canonicalScope, RuleSetVersion = canonicalScope is null ? null : CorrelationRules.RuleSetVersion,
            CurrentFeatureKeys = currentKeys, BaselineFeatureKeys = baselineKeys, SourceInBaseline = sourceInBaseline, ScopeInBaseline = scopeInBaseline,
            WorkType = work.WorkType == DeliveryWorkType.All ? null : work.WorkType, Quality = work.Quality,
            ClassificationRuleSet = work.IsActive ? FunctionalQualityClassifier.RuleSetVersion : null,
        };
        return view with
        {
            Scope = scope, Notices = notices, Members = memberRows,
            Candidates = [.. (selectedScope.Candidates ?? []).Where(c => SideKeys(current, filter.SourceId, _ => true).Contains(c.FeatureKey))],
        };
    }
}
