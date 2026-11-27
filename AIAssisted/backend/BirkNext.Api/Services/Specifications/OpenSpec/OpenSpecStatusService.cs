using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Specifications;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>
/// Read-only views over a project's OpenSpec source (2026-11-27): the source status (current / proposed / archived, inventory, diagnostics), the
/// source-change report (previous effective baseline → current effective baseline, and prospective proposal impact, kept apart) and the Data
/// Quality reconciliation (reference counts computed independently of the dashboard aggregation). Nothing is written; cases are never rewritten.
/// </summary>
public sealed class OpenSpecStatusService(SpecificationCatalogService catalogs, OpenSpecSourceModelStore models, SpecificationFormatSettingsStore settings,
    AppDbContext db, BirkNext.Api.Services.SourceAnalysis.SourceBaselineService? baselines = null, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    // ── Status ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OpenSpecSourceStatus> StatusAsync(string projectKey, CancellationToken ct)
    {
        var catalog = await catalogs.CurrentAsync(projectKey, ct);
        var problem = models.Problem(projectKey);
        var format = SpecificationFormatMarker.FormatOf(catalog?.Fingerprint);
        var model = catalog is not null && format == SpecificationFormat.OpenSpec ? models.Find(projectKey, catalog.Fingerprint) : null;
        var application = model?.ApplicationKey ?? await ApplicationOfAsync(catalog?.SnapshotId, ct);
        SpecificationFormat? configured = null;
        var notes = new List<string>();
        try { configured = application is null ? null : settings.FormatOf(application).Format; }
        catch (InvalidOperationException e) { notes.Add(e.Message); }
        if (configured is { } c && catalog is not null && c != format)
            notes.Add($"Applikasjonen er nå satt til {SpecificationFormatMarker.Label(c)}, men gjeldende katalog (revisjon {catalog.Revision}) ble behandlet som {SpecificationFormatMarker.Label(format)}. Formatvalget gjelder fremtidige importer; importer kilden på nytt for å behandle den som {SpecificationFormatMarker.Label(c)}.");
        if (catalog is not null && format == SpecificationFormat.OpenSpec && model is null)
            notes.Add("OpenSpec-kildemodellen for denne katalogen er ikke tilgjengelig (IKKE VERIFISERT): forslag, arkiv og diagnostikk kan ikke vises. Gjeldende krav og scenarioer i katalogen er uendret.");
        var status = new OpenSpecSourceStatus
        {
            ProjectKey = projectKey, CatalogFormat = format, ConfiguredFormat = configured, ApplicationKey = application, CatalogRevision = catalog?.Revision,
            CapturedAt = catalog?.CapturedAt, ModelAvailable = model is not null,
            RouteProblem = problem is not null && (catalog is null || problem.At > catalog.CapturedAt) ? problem.Message : null, Notes = notes,
        };
        if (model is null) return status;
        return status with
        {
            SchemaName = model.SchemaName, SchemaState = model.SchemaState, Inventory = Inventory(model),
            CurrentRequirements = model.CurrentRequirements.Count(), CurrentScenarios = model.CurrentRequirements.Sum(r => r.Scenarios.Count),
            CurrentRequirementsWithoutScenarios = model.CurrentRequirements.Count(r => r.Scenarios.Count == 0),
            Capabilities = [.. model.Capabilities.Select(c => new OpenSpecCapabilityView(c.Key, c.Path, c.Requirements.Count, c.Requirements.Sum(r => r.Scenarios.Count), c.Requirements.Count(r => r.Scenarios.Count == 0)))],
            ActiveChanges = [.. model.ActiveChanges.Select(View)], ArchivedChanges = [.. model.ArchivedChanges.Select(View)],
            Diagnostics = model.Diagnostics,
        };
    }

    private async Task<string?> ApplicationOfAsync(Guid? snapshotId, CancellationToken ct)
    {
        if (snapshotId is not { } id || baselines is null) return null;
        var snapshot = (await baselines.SnapshotsAsync(ct)).FirstOrDefault(s => s.Id == id);
        return snapshot is null ? null : BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.KeyOf(snapshot);
    }

    public static OpenSpecDocumentInventory Inventory(OpenSpecSourceModel m)
    {
        int N(params OpenSpecDocumentKind[] kinds) => m.Documents.Count(d => kinds.Contains(d.Kind));
        return new OpenSpecDocumentInventory
        {
            Discovered = m.Documents.Count, CurrentSpecifications = N(OpenSpecDocumentKind.CurrentSpecification), ActiveChangeDocuments = N(OpenSpecDocumentKind.ActiveChangeDocument),
            ActiveDeltaSpecifications = N(OpenSpecDocumentKind.ActiveDeltaSpecification),
            ArchivedChangeDocuments = N(OpenSpecDocumentKind.ArchivedChangeDocument, OpenSpecDocumentKind.ArchivedDeltaSpecification),
            SupportingDocuments = N(OpenSpecDocumentKind.SupportingDocument), Configuration = N(OpenSpecDocumentKind.Configuration),
            NotOpenSpec = N(OpenSpecDocumentKind.NotOpenSpec), Unsupported = N(OpenSpecDocumentKind.Unsupported),
        };
    }

    private static OpenSpecChangeView View(OpenSpecChange c) => new()
    {
        ChangeKey = c.Key, ChangeId = c.ChangeId, State = c.State, Title = c.Title, Documents = c.Documents,
        Deltas = [.. c.Deltas.Select(d => new OpenSpecDeltaView
        {
            Operation = d.Operation, Capability = d.Capability, RequirementName = d.RequirementName, RenamedTo = d.RenamedTo, BaselineRequirementKey = d.BaselineRequirementKey,
            Resolution = d.Resolution, Scenarios = d.Content?.Scenarios.Count ?? 0, Path = d.Path, Line = d.Line,
        })],
    };

    // ── Source changes (Phase 11) ──────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OpenSpecSourceChangeReport> SourceChangesAsync(string projectKey, CancellationToken ct)
    {
        var revisions = (await catalogs.AllRevisionsAsync(projectKey, 50, ct)).Where(r => SpecificationFormatMarker.IsOpenSpec(r.Fingerprint)).ToList();
        var notes = new List<string>();
        if (revisions.Count == 0)
            return new() { ProjectKey = projectKey, Notes = ["Prosjektet har ingen OpenSpec-katalog. Kildeendringer for Spec Kit vises som før under Kildeendringer."] };
        var current = models.Find(projectKey, revisions[0].Fingerprint);
        var previous = revisions.Count > 1 ? models.Find(projectKey, revisions[1].Fingerprint) : null;
        if (current is null)
            return new() { ProjectKey = projectKey, CurrentRevision = revisions[0].Revision, Notes = ["OpenSpec-kildemodellen for gjeldende katalog er ikke tilgjengelig: kildeendringer er IKKE VERIFISERT."] };
        if (revisions.Count == 1) notes.Add("Bare én OpenSpec-baseline er importert: ingen tidligere baseline å sammenligne med.");
        else if (previous is null) notes.Add($"Kildemodellen for forrige baseline (revisjon {revisions[1].Revision}) er ikke tilgjengelig: baselineendringer er IKKE VERIFISERT.");
        var cases = await CasesAsync(projectKey, ct);
        var baseline = previous is null ? [] : Compare(previous, current);
        var proposed = Prospective(current);
        return new OpenSpecSourceChangeReport
        {
            ProjectKey = projectKey, CurrentRevision = revisions[0].Revision, PreviousRevision = previous is null ? null : revisions[1].Revision,
            BaselineChanges = [.. baseline.Select(c => c with { AffectedCases = Affected(c, cases) })],
            ProposedImpact = [.. proposed.Select(c => c with { AffectedCases = Affected(c, cases) })],
            Notes = notes,
        };
    }

    /// <summary>Accepted baseline changes between two effective baselines. A rename is reported only with explicit evidence: an archived RENAMED
    /// delta that is new in the current import, naming exactly the removed (old) and the added (new) requirement. Never by title similarity.</summary>
    public static List<OpenSpecSourceChange> Compare(OpenSpecSourceModel previous, OpenSpecSourceModel current)
    {
        var before = previous.CurrentRequirements.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var after = current.CurrentRequirements.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var knownArchives = previous.ArchivedChanges.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var renames = current.ArchivedChanges.Where(a => !knownArchives.Contains(a.Key)).SelectMany(a => a.Deltas.Where(d => d.Operation == OpenSpecDeltaOperation.Renamed && d.RenamedTo is not null)
            .Select(d => (Change: a.ChangeId, Old: RequirementKeyIn(d.Capability, d.RequirementName), New: RequirementKeyIn(d.Capability, d.RenamedTo!)))).ToList();
        var changes = new List<OpenSpecSourceChange>();
        var renamedOld = new HashSet<string>(StringComparer.Ordinal); var renamedNew = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (change, oldKey, newKey) in renames)
            if (before.ContainsKey(oldKey) && !after.ContainsKey(oldKey) && after.TryGetValue(newKey, out var renamed) && !before.ContainsKey(newKey))
            {
                renamedOld.Add(oldKey); renamedNew.Add(newKey);
                changes.Add(new() { Kind = OpenSpecChangeKind.RenamedRequirement, Capability = renamed.Capability, Key = newKey, PreviousKey = oldKey, Name = renamed.Name,
                    Evidence = $"Arkivert endring «{change}»: RENAMED «{before[oldKey].Name}» → «{renamed.Name}» ({renamed.Path}:{renamed.Line}). Ny identitet; testtilfeller knyttet til den gamle flyttes ikke automatisk.", ChangeId = change });
            }
        foreach (var (key, r) in after.Where(x => !before.ContainsKey(x.Key) && !renamedNew.Contains(x.Key)))
            changes.Add(new() { Kind = OpenSpecChangeKind.NewRequirement, Capability = r.Capability, Key = key, Name = r.Name, Evidence = $"{r.Path}:{r.Line}" });
        foreach (var (key, r) in before.Where(x => !after.ContainsKey(x.Key) && !renamedOld.Contains(x.Key)))
            changes.Add(new() { Kind = OpenSpecChangeKind.RemovedRequirement, Capability = r.Capability, Key = key, Name = r.Name, Evidence = $"Fantes i forrige baseline ({r.Path}:{r.Line}); finnes ikke i gjeldende." });
        foreach (var (key, now) in after.Where(x => before.ContainsKey(x.Key)))
        {
            var was = before[key];
            if (was.Statement != now.Statement)
                changes.Add(new() { Kind = OpenSpecChangeKind.ModifiedRequirement, Capability = now.Capability, Key = key, Name = now.Name, Evidence = $"Kravteksten er endret ({now.Path}:{now.Line})." });
            var oldScenarios = was.Scenarios.ToDictionary(s => s.Key, StringComparer.Ordinal);
            var newScenarios = now.Scenarios.ToDictionary(s => s.Key, StringComparer.Ordinal);
            foreach (var s in now.Scenarios.Where(s => !oldScenarios.ContainsKey(s.Key)))
                changes.Add(new() { Kind = OpenSpecChangeKind.NewScenario, Capability = now.Capability, Key = s.Key, Name = s.Name, Evidence = $"{now.Path}:{s.Line}" });
            foreach (var s in was.Scenarios.Where(s => !newScenarios.ContainsKey(s.Key)))
                changes.Add(new() { Kind = OpenSpecChangeKind.RemovedScenario, Capability = now.Capability, Key = s.Key, Name = s.Name, Evidence = $"Fantes i forrige baseline ({was.Path}:{s.Line})." });
            foreach (var s in now.Scenarios.Where(s => oldScenarios.TryGetValue(s.Key, out var o) && o.TextSha256 != s.TextSha256))
                changes.Add(new() { Kind = OpenSpecChangeKind.ModifiedScenario, Capability = now.Capability, Key = s.Key, Name = s.Name, Evidence = $"Stegene er endret ({now.Path}:{s.Line})." });
        }
        return [.. changes.OrderBy(c => c.Capability, StringComparer.Ordinal).ThenBy(c => c.Kind).ThenBy(c => c.Key, StringComparer.Ordinal)];

        static string RequirementKeyIn(string scopedCapability, string name) => $"{SpecificationFormatMarker.OpenSpecKeyPrefix}{scopedCapability}/{OpenSpecIdentity.Slug(name)}";
    }

    /// <summary>The prospective impact of active proposals on the current baseline (never applied; conflicts and unresolved targets stay visible).</summary>
    public static List<OpenSpecSourceChange> Prospective(OpenSpecSourceModel current)
    {
        var baseline = current.CurrentRequirements.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var list = new List<OpenSpecSourceChange>();
        foreach (var change in current.ActiveChanges)
            foreach (var d in change.Deltas)
            {
                var suffix = d.Resolution is "Resolved" or "NewRequirement" ? "" : $" — {ResolutionText(d.Resolution)}";
                var evidence = $"Forslag «{change.ChangeId}», {d.Operation.ToString().ToUpperInvariant()} ({d.Path}:{d.Line}){suffix}";
                switch (d.Operation)
                {
                    case OpenSpecDeltaOperation.Added:
                        list.Add(new() { Kind = OpenSpecChangeKind.NewRequirement, Capability = d.Capability, Key = d.Content?.Key ?? "", Name = d.RequirementName, Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                        break;
                    case OpenSpecDeltaOperation.Removed:
                        list.Add(new() { Kind = OpenSpecChangeKind.RemovedRequirement, Capability = d.Capability, Key = d.BaselineRequirementKey ?? "", Name = d.RequirementName, Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                        break;
                    case OpenSpecDeltaOperation.Renamed:
                        list.Add(new() { Kind = OpenSpecChangeKind.RenamedRequirement, Capability = d.Capability, Key = d.BaselineRequirementKey ?? "", PreviousKey = d.BaselineRequirementKey, Name = $"{d.RequirementName} → {d.RenamedTo}", Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                        break;
                    case OpenSpecDeltaOperation.Modified:
                        list.Add(new() { Kind = OpenSpecChangeKind.ModifiedRequirement, Capability = d.Capability, Key = d.BaselineRequirementKey ?? "", Name = d.RequirementName, Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                        if (d.BaselineRequirementKey is { } key && baseline.TryGetValue(key, out var was) && d.Content is { } proposed)
                        {
                            // Scenario keys of the proposed content are computed in the same namespace, so the comparison is per named scenario.
                            var old = was.Scenarios.ToDictionary(s => s.Key, StringComparer.Ordinal);
                            var next = proposed.Scenarios.ToDictionary(s => s.Key, StringComparer.Ordinal);
                            foreach (var s in proposed.Scenarios.Where(s => !old.ContainsKey(s.Key)))
                                list.Add(new() { Kind = OpenSpecChangeKind.NewScenario, Capability = d.Capability, Key = s.Key, Name = s.Name, Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                            foreach (var s in was.Scenarios.Where(s => !next.ContainsKey(s.Key)))
                                list.Add(new() { Kind = OpenSpecChangeKind.RemovedScenario, Capability = d.Capability, Key = s.Key, Name = s.Name, Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                            foreach (var s in proposed.Scenarios.Where(s => old.TryGetValue(s.Key, out var o) && o.TextSha256 != s.TextSha256))
                                list.Add(new() { Kind = OpenSpecChangeKind.ModifiedScenario, Capability = d.Capability, Key = s.Key, Name = s.Name, Evidence = evidence, Prospective = true, ChangeId = change.ChangeId });
                        }
                        break;
                }
            }
        return list;
    }

    private static string ResolutionText(string resolution) => resolution switch
    {
        "Unresolved" => "målet finnes ikke i gjeldende grunnlag",
        "Conflict" => "i konflikt med en annen aktiv endring",
        "TargetExists" => "målet finnes allerede",
        _ => resolution,
    };

    private sealed record SavedCase(Guid PlanId, string PlanName, string CaseId, string Title, string? SourceIdentity, List<string> RequirementIds);

    private async Task<List<SavedCase>> CasesAsync(string projectKey, CancellationToken ct)
    {
        var owner = "project:" + projectKey;
        var rows = await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == owner).ToListAsync(ct);
        var list = new List<SavedCase>();
        foreach (var row in rows)
        {
            TestPlanDraft? plan;
            try { plan = JsonSerializer.Deserialize<TestPlanDraft>(row.ContentJson, TestPlanning.TestPlanSuggestionEngine.Json); }
            catch (JsonException) { continue; }
            if (plan is null) continue;
            list.AddRange(plan.Cases.Select(c => new SavedCase(row.Id, row.PlanName, c.Source.ScenarioId, c.EffectiveTitle, c.Generated.SourceIdentity,
                [.. c.Generated.Requirements.Select(r => r.RuleId)])));
        }
        return list;
    }

    /// <summary>Cases affected by a change, ONLY through verified identities: the case's own source identity is the changed scenario's (or the
    /// changed requirement's) identity, or the case names the requirement key exactly. Nothing is rewritten; the case is flagged for review.</summary>
    private static List<OpenSpecAffectedCase> Affected(OpenSpecSourceChange change, List<SavedCase> cases)
    {
        var keys = new[] { change.Key, change.PreviousKey }.Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return [];
        var identities = keys.ToDictionary(k => D.SourceIdentityOf(D.Specification, OpenSpecIdentity.SourceIdentityKey(k)), k => k, StringComparer.Ordinal);
        var requirementLevel = change.Kind is OpenSpecChangeKind.NewRequirement or OpenSpecChangeKind.ModifiedRequirement or OpenSpecChangeKind.RemovedRequirement or OpenSpecChangeKind.RenamedRequirement;
        var result = new List<OpenSpecAffectedCase>();
        foreach (var c in cases)
        {
            if (c.SourceIdentity is { } id && identities.TryGetValue(id, out var key))
                result.Add(new(c.PlanId, c.PlanName, c.CaseId, c.Title, $"Testtilfellets kildeidentitet {id} er {key}."));
            else if (requirementLevel && c.RequirementIds.FirstOrDefault(r => keys.Contains(r, StringComparer.OrdinalIgnoreCase)) is { } named)
                result.Add(new(c.PlanId, c.PlanName, c.CaseId, c.Title, $"Testtilfellet er knyttet til kravet {named}."));
            else if (requirementLevel && c.SourceIdentity is { } sid && keys.Any(k => sid == D.SourceIdentityOf(D.Specification, OpenSpecIdentity.SourceIdentityKey(k))))
                result.Add(new(c.PlanId, c.PlanName, c.CaseId, c.Title, $"Testtilfellets kildeidentitet {sid} er kravet."));
        }
        return result;
    }

    // ── Reconciliation (Data Quality, Phase 13) ─────────────────────────────────────────────────────────────────────────────

    public async Task<OpenSpecReconciliationReport> ReconcileAsync(string projectKey, CancellationToken ct)
    {
        var catalog = await catalogs.CurrentAsync(projectKey, ct);
        if (catalog is null)
            return new() { ProjectKey = projectKey, ComputedAt = Now, Overall = ReconciliationStatus.NotApplicable, Notes = ["Prosjektet har ingen spesifikasjonskatalog."] };
        var format = SpecificationFormatMarker.FormatOf(catalog.Fingerprint);
        if (format != SpecificationFormat.OpenSpec)
            return new() { ProjectKey = projectKey, CatalogFormat = format, CatalogRevision = catalog.Revision, ComputedAt = Now, Overall = ReconciliationStatus.NotApplicable,
                Notes = ["Katalogen er behandlet som Spec Kit. OpenSpec-avstemmingen gjelder ikke; Spec Kit-datakvalitet er uendret."] };
        var model = models.Find(projectKey, catalog.Fingerprint);
        var evidence = await catalogs.EvidenceOfAsync(catalog.Id, ct) ?? [];
        var stored = await catalogs.EvidenceAsync(null, projectKey, ct);
        var report = Reconcile(model, catalog, evidence);
        // Dashboard population: what Testoversikt reads (the project's newest catalog with evidence) must be this catalog's evidence.
        var dashboard = stored is { } s && s.Catalog.Id == catalog.Id
            ? new ReconciliationCheck("D08", "Testoversikt leser denne katalogens evidens", ReconciliationStatus.Pass, catalog.Revision.ToString(), s.Catalog.Revision.ToString(), "Samme katalogrevisjon.")
            : new ReconciliationCheck("D08", "Testoversikt leser denne katalogens evidens", ReconciliationStatus.Fail, catalog.Revision.ToString(), stored?.Catalog.Revision.ToString() ?? "ingen", "Testoversikt ville lest en annen revisjon.");
        var checks = report.Checks.Append(dashboard).ToList();
        return report with { ProjectKey = projectKey, ComputedAt = Now, Checks = checks, Overall = Overall(checks) };
    }

    /// <summary>
    /// Pure reconciliation of one OpenSpec catalog. Reference counts come from an independent scan of the stored original Markdown (heading counts
    /// per lifecycle bucket), not from the parser or the dashboard; dashboard-facing counts come from the stored evidence through discovery's own
    /// mapping. A check without evidence is NOT VERIFIED, never PASS.
    /// </summary>
    public static OpenSpecReconciliationReport Reconcile(OpenSpecSourceModel? model, SpecificationCatalogView catalog, List<StoredSpecificationEvidence> evidence)
    {
        var checks = new List<ReconciliationCheck>();
        void Check(string id, string title, bool? ok, object expected, object observed, string detail) =>
            checks.Add(new(id, title, ok is null ? ReconciliationStatus.NotVerified : ok.Value ? ReconciliationStatus.Pass : ReconciliationStatus.Fail, expected.ToString() ?? "", observed.ToString() ?? "", detail));
        if (model is null)
        {
            Check("D00", "OpenSpec-kildemodell tilgjengelig", null, "modell", "mangler", "Kildemodellen er ikke tilgjengelig; avstemming mot kilden kan ikke gjøres.");
            return new() { CatalogFormat = SpecificationFormat.OpenSpec, CatalogRevision = catalog.Revision, Checks = checks, Overall = Overall(checks) };
        }
        var inventory = Inventory(model);
        var bucketed = inventory.CurrentSpecifications + inventory.ActiveChangeDocuments + inventory.ActiveDeltaSpecifications + inventory.ArchivedChangeDocuments
            + inventory.SupportingDocuments + inventory.Configuration + inventory.NotOpenSpec + inventory.Unsupported;
        Check("D01a", "Alle dokumenter er klassifisert i nøyaktig én kategori", bucketed == inventory.Discovered, inventory.Discovered, bucketed,
            $"Gjeldende {inventory.CurrentSpecifications}, aktive endringsdokumenter {inventory.ActiveChangeDocuments}, aktive delta {inventory.ActiveDeltaSpecifications}, arkiv {inventory.ArchivedChangeDocuments}, støtte {inventory.SupportingDocuments}, konfigurasjon {inventory.Configuration}, utenfor OpenSpec {inventory.NotOpenSpec}, ikke støttet {inventory.Unsupported}.");

        // Independent reference scan (headings outside fenced code, per lifecycle bucket) of the stored original Markdown.
        var currentDocs = model.Documents.Where(d => d.Kind == OpenSpecDocumentKind.CurrentSpecification).ToList();
        var missingText = currentDocs.Where(d => !model.OriginalMarkdown.ContainsKey(d.Path)).Select(d => d.Path).ToList();
        var reference = currentDocs.Where(d => model.OriginalMarkdown.ContainsKey(d.Path)).Select(d => ReferenceScan(model.OriginalMarkdown[d.Path])).ToList();
        var refRequirements = reference.Sum(r => r.Requirements);
        var refScenarios = reference.Sum(r => r.ScenariosUnderRequirement);
        var parsedRequirements = model.CurrentRequirements.Count();
        var parsedScenarios = model.CurrentRequirements.Sum(r => r.Scenarios.Count);
        var currentPaths = currentDocs.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var excludedRequirements = model.Exclusions.Where(e => currentPaths.Contains(e.Path)).Sum(e => e.Requirements);
        var excludedScenarios = model.Exclusions.Where(e => currentPaths.Contains(e.Path)).Sum(e => e.Scenarios);
        var refAllRequirements = refRequirements + reference.Sum(r => r.RequirementsUnderDeltaSections);
        var refAllScenarios = refScenarios + reference.Sum(r => r.ScenariosUnderDeltaSections);
        var verifiable = missingText.Count == 0 ? (bool?)true : null;
        Check("D01", "Gjeldende krav: referansetelling = tolket + rapportert utelatt", verifiable is null ? null : refAllRequirements == parsedRequirements + excludedRequirements,
            refAllRequirements, $"{parsedRequirements} tolket + {excludedRequirements} utelatt med diagnostikk",
            missingText.Count == 0 ? "Uavhengig overskriftstelling («### Requirement:» utenfor kodeblokker) i gjeldende spesifikasjoner." : $"Originaltekst mangler for {missingText.Count} dokument(er): IKKE VERIFISERT.");
        Check("D05", "Gjeldende scenarioer: referansetelling = tolket + rapportert utelatt", verifiable is null ? null : refAllScenarios == parsedScenarios + excludedScenarios,
            refAllScenarios, $"{parsedScenarios} tolket + {excludedScenarios} utelatt med diagnostikk",
            "Scenarioer under et krav. Krav uten scenarioer: " + model.CurrentRequirements.Count(r => r.Scenarios.Count == 0) + ".");
        var orphans = reference.Sum(r => r.OrphanScenarios);
        Check("D06", "Foreldreløse scenarioer er rapportert", verifiable is null ? null : orphans == model.Diagnostics.Count(d => d.Code == "ORPHAN_SCENARIO" && currentDocs.Any(c => c.Path == d.Path)),
            orphans, model.Diagnostics.Count(d => d.Code == "ORPHAN_SCENARIO" && currentDocs.Any(c => c.Path == d.Path)), "Hvert foreldreløst scenario skal ha en ORPHAN_SCENARIO-diagnostikk.");

        // Catalog and evidence = current baseline only.
        var evidenceRequirements = evidence.Sum(e => e.Evidence.Requirements.Count);
        var evidenceScenarios = evidence.Sum(e => e.Evidence.AcceptanceScenarios.Count);
        Check("D01b", "Katalogens krav = gjeldende tolkede krav", catalog.Requirements.Count == Math.Min(parsedRequirements, OpenSpecLimits.MaxRequirements), parsedRequirements, catalog.Requirements.Count, "Katalogen holder bare gjeldende grunnlag.");
        Check("D01c", "Evidensens krav og scenarioer = gjeldende tolkede", evidenceRequirements == parsedRequirements && evidenceScenarios == parsedScenarios,
            $"{parsedRequirements} krav / {parsedScenarios} scenarioer", $"{evidenceRequirements} krav / {evidenceScenarios} scenarioer", "Discovery og Testoversikt leser denne evidensen.");
        var proposalKeys = model.ActiveChanges.SelectMany(c => c.Deltas).Where(d => d.Operation == OpenSpecDeltaOperation.Added && d.Resolution == "NewRequirement").Select(d => d.Content?.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var evidenceIds = evidence.SelectMany(e => e.Evidence.Requirements.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
        var nonCurrentPaths = evidence.Count(e => model.Documents.FirstOrDefault(d => SpecificationCatalogRules.SamePath(d.Path, e.Path))?.Kind != OpenSpecDocumentKind.CurrentSpecification);
        Check("D02", "Forslag er telt separat og ikke i gjeldende populasjon", !proposalKeys.Overlaps(evidenceIds) && nonCurrentPaths == 0,
            "0 forslag i evidens", $"{proposalKeys.Count(evidenceIds.Contains)} forslagskrav, {nonCurrentPaths} ikke-gjeldende dokumenter i evidens",
            $"{model.ActiveChanges.Count} aktive endringer, {model.ActiveChanges.Sum(c => c.Deltas.Count)} operasjoner — vist som foreslåtte endringer.");
        Check("D03", "Arkiv er kun historikk", !evidence.Any(e => e.Path.Contains("/changes/archive/", StringComparison.OrdinalIgnoreCase)),
            "0 arkivdokumenter i evidens", evidence.Count(e => e.Path.Contains("/changes/archive/", StringComparison.OrdinalIgnoreCase)),
            $"{model.ArchivedChanges.Count} arkiverte endringer, ikke anvendt på nytt.");

        // Identities through discovery's own mapping (what Testoversikt and Finn og forstå use).
        const string project = "reconcile";
        var candidates = evidence.SelectMany(e => D.FromSpecification(project, e.Evidence).Candidates).Where(c => c.CapabilityId == D.Specification).ToList();
        var expectedCandidates = parsedScenarios + model.CurrentRequirements.Count(r => r.Scenarios.Count == 0);
        Check("D07a", "Kandidatpopulasjon = scenarioer + krav uten scenarioer", candidates.Count == expectedCandidates, expectedCandidates, candidates.Count,
            "Referanseverdien er beregnet fra kildemodellen, uavhengig av dashboardaggregeringen.");
        var duplicateIdentities = candidates.GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).Count(g => g.Count() > 1);
        Check("D04", "Ingen dupliserte kanoniske identiteter", duplicateIdentities == 0, 0, duplicateIdentities, "Ulike capabilities med like kravnavn har ulike identiteter.");
        var generatedOnly = candidates.All(c => c.RequirementIds.All(SpecificationFormatMarker.IsGeneratedOpenSpecKey));
        Check("D04b", "Ingen kandidater bruker oppdiktede kildeidentifikatorer", generatedOnly, "bare os:-nøkler", generatedOnly ? "bare os:-nøkler" : "andre ID-er funnet",
            "OpenSpec-krav har ingen FR-ID; BirkNext-nøkler er merket os:.");
        var expectedIdentities = model.CurrentRequirements.SelectMany(r => r.Scenarios.Count == 0 ? [r.Key] : r.Scenarios.Select(s => s.Key))
            .Select(k => D.SourceIdentityOf(D.Specification, OpenSpecIdentity.SourceIdentityKey(k))).ToHashSet(StringComparer.Ordinal);
        var drill = candidates.Count(c => expectedIdentities.Contains(c.SourceIdentity));
        Check("D08a", "Hvert scenario kan slås opp på sin identitet (drill-down)", drill == expectedIdentities.Count && candidates.Count == expectedIdentities.Count,
            expectedIdentities.Count, drill, "Identitet = openspec/1|nøkkel; samme som lagrede testtilfeller bærer.");
        Check("D07", "Modulgruppering: OpenSpec-capabilities er prosjektnivå/uten modul", true, ModuleStructure.Unassigned, ModuleStructure.Unassigned,
            "Capability er ikke en BirkNext-modul; Testoversikt grupperer OpenSpec-spesifikasjoner som «Uten modultilknytning».");

        // Lifecycle diagnostics (reported, never resolved).
        int Count(string code) => model.Diagnostics.Count(d => d.Code == code);
        Check("L06", "Konflikter mellom aktive endringer er rapportert", true, "rapportert", Count("CONFLICTING_ACTIVE_CHANGES"), "Konflikter løses aldri automatisk.");
        Check("L08", "Uavklarte delta-mål er rapportert", true, "rapportert", Count("UNRESOLVED_DELTA_TARGET") + Count("ADDED_TARGET_EXISTS"), "MODIFIED/REMOVED/RENAMED uten mål, ADDED som finnes.");
        Check("D06b", "Ikke-støttede kilder er synlige", Count("UNSUPPORTED_DOCUMENT") == inventory.Unsupported, inventory.Unsupported, Count("UNSUPPORTED_DOCUMENT"), "Hvert ikke-støttet dokument har en diagnostikk.");
        Check("S00", "Skjema verifisert", model.SchemaState == "BuiltIn" ? true : null, "innebygd spec-driven", model.SchemaName ?? "(ingen config)",
            model.SchemaState == "BuiltIn" ? "Innebygd struktur." : "Egendefinert skjema: strukturen er IKKE VERIFISERT.");
        Check("V01", "Kildeversjon konsistent", model.SnapshotId == catalog.SnapshotId && model.ArchiveSha256 == catalog.ArchiveSha256,
            $"{catalog.SnapshotId}/{catalog.ArchiveSha256?[..Math.Min(12, catalog.ArchiveSha256.Length)]}", $"{model.SnapshotId}/{model.ArchiveSha256?[..Math.Min(12, model.ArchiveSha256.Length)]}",
            "Modellen og katalogen er fra samme import.");
        return new() { CatalogFormat = SpecificationFormat.OpenSpec, CatalogRevision = catalog.Revision, Checks = checks, Overall = Overall(checks) };
    }

    private static ReconciliationStatus Overall(List<ReconciliationCheck> checks) =>
        checks.Any(c => c.Status == ReconciliationStatus.Fail) ? ReconciliationStatus.Fail
        : checks.Any(c => c.Status == ReconciliationStatus.NotVerified) ? ReconciliationStatus.NotVerified : ReconciliationStatus.Pass;

    public sealed record ReferenceCounts(int Requirements, int ScenariosUnderRequirement, int OrphanScenarios, int RequirementsUnderDeltaSections, int ScenariosUnderDeltaSections);

    /// <summary>An independent, deliberately simple heading count (not the route's parser): «### Requirement:» and «#### Scenario:» outside fenced code,
    /// attributing a scenario to the nearest preceding requirement heading in the same H2 section.</summary>
    public static ReferenceCounts ReferenceScan(string markdown)
    {
        int req = 0, scen = 0, orphan = 0, deltaReq = 0, deltaScen = 0;
        var fenced = false; var inRequirement = false; var inDelta = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.TrimStart();
            if (t.StartsWith("```") || t.StartsWith("~~~")) { fenced = !fenced; continue; }
            if (fenced || !raw.StartsWith('#')) continue;
            var hashes = raw.TakeWhile(c => c == '#').Count();
            var text = raw[hashes..].Trim();
            if (hashes <= 2)
            {
                inRequirement = false;
                inDelta = hashes == 2 && System.Text.RegularExpressions.Regex.IsMatch(text, @"^(ADDED|MODIFIED|REMOVED|RENAMED)\s+Requirements?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            else if (hashes == 3)
            {
                inRequirement = text.StartsWith("Requirement", StringComparison.OrdinalIgnoreCase) && text.Contains(':');
                if (inRequirement) { if (inDelta) deltaReq++; else req++; }
            }
            else if (hashes == 4 && text.StartsWith("Scenario", StringComparison.OrdinalIgnoreCase) && text.Contains(':'))
            {
                if (!inRequirement) orphan++;
                else if (inDelta) deltaScen++;
                else scen++;
            }
        }
        return new(req, scen, orphan, deltaReq, deltaScen);
    }

    /// <summary>The reconciliation report as Markdown (export).</summary>
    public static string ToMarkdown(OpenSpecReconciliationReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# OpenSpec-avstemming — {r.ProjectKey}").AppendLine();
        sb.AppendLine($"- Katalogformat: {SpecificationFormatMarker.Label(r.CatalogFormat)}; revisjon: {r.CatalogRevision?.ToString() ?? "–"}");
        sb.AppendLine($"- Beregnet: {r.ComputedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- Samlet: **{r.Overall}**").AppendLine();
        sb.AppendLine("| ID | Kontroll | Status | Forventet | Observert | Detalj |").AppendLine("|---|---|---|---|---|---|");
        foreach (var c in r.Checks) sb.AppendLine($"| {c.Id} | {Cell(c.Title)} | {c.Status} | {Cell(c.Expected)} | {Cell(c.Observed)} | {Cell(c.Detail)} |");
        foreach (var n in r.Notes) sb.AppendLine().AppendLine($"> {n}");
        return sb.ToString();
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    }
}
