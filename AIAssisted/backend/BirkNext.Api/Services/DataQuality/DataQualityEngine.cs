using System.Security.Cryptography;
using System.Text;
using BirkNext.DataQuality;
using BirkNext.TestPlanning;
using R = BirkNext.Api.Services.DataQuality.ReferenceCalculator;

namespace BirkNext.Api.Services.DataQuality;

/// <summary>
/// Runs one read-only data-quality audit (System Settings → Data Quality, 2026-11-27). The stages are kept apart:
/// <list type="number">
/// <item>Source → stored: stored document text, read again by <see cref="ReferenceSpecParser"/>, against the stored parse.</item>
/// <item>Stored → API: <see cref="ReferenceCalculator"/> (independent) against the production overview API.</item>
/// <item>API → UI: expected figures for the browser, which computes the production values from the same API response.</item>
/// </list>
/// Nothing in the business domain is written. A change of the data during the run makes it INCONCLUSIVE (never PASS).
/// </summary>
public sealed class DataQualityEngine(Func<string, bool, CancellationToken, Task<DataQualitySnapshot>> read, Func<string, CancellationToken, Task<string>> marker,
    Func<string, CancellationToken, Task<ManualTestingOverview>> overview,
    Func<string, CancellationToken, Task<BirkNext.Specifications.OpenSpecReconciliationReport?>>? openSpec = null)
{
    public const int MaxRecordsPerCheck = 200;

    public DataQualityEngine(DataQualitySnapshotReader reader, Func<string, CancellationToken, Task<ManualTestingOverview>> overview,
        Func<string, CancellationToken, Task<BirkNext.Specifications.OpenSpecReconciliationReport?>>? openSpec = null)
        : this(reader.ReadAsync, reader.MarkerAsync, overview, openSpec) { }

    public async Task<DataQualityReport> RunAsync(string runId, string projectKey, IReadOnlyCollection<DataQualityCategory> categories, Action<DataQualityProgress> progress, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var checks = new List<DataQualityCheck>(); var phases = new List<string>(); var skipped = new List<string>(); var followUps = new List<string>();
        void Phase(string name) => progress(new DataQualityProgress { Phase = name, ChecksCompleted = checks.Count, ChecksFailed = checks.Count(c => c.Status == DataQualityStatus.Fail), PhasesDone = [.. phases] });
        void Done(string name) { phases.Add(name); Phase(name); }

        Phase("Leser et konsistent øyeblikksbilde (skrivebeskyttet)");
        var snapshot = await read(projectKey, categories.Contains(DataQualityCategory.SourceImport), ct);
        var expected = R.Calculate(snapshot);
        var baseline = Baseline(snapshot, expected);
        Done("Øyeblikksbilde lest");
        checks.Add(BaselineCheck(snapshot, expected, baseline));

        // OpenSpec (2026-11-27): the reference rules below are Spec Kit's (stories + numbers, FR ids, <module>/specs). For a catalog the OpenSpec route
        // produced they would report false differences, so the OpenSpec reconciliation (its own independent reference scan) replaces them — said in
        // the report, never silently. A Spec Kit catalog (or no OpenSpec reader) runs exactly as before.
        BirkNext.Specifications.OpenSpecReconciliationReport? os = null;
        if (openSpec is not null)
        {
            try { os = await openSpec(projectKey, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(Check("openspec.format", DataQualityCategory.SourceImport, DataQualityStage.SourceToStored, "Spesifikasjonsformat", projectKey, "", null, null,
                    DataQualityStatus.NotVerified, DataQualityCause.SourceUnavailable, $"Spesifikasjonsformatet kunne ikke leses ({ex.GetType().Name}); kontrollene under bruker Spec Kit-reglene."));
            }
        }
        var openSpecCatalog = os is { CatalogFormat: BirkNext.Specifications.SpecificationFormat.OpenSpec };
        if (openSpecCatalog)
        {
            Phase("OpenSpec-avstemming");
            checks.AddRange(OpenSpecChecks(os!, projectKey));
            skipped.Add("Spec Kit-referansekontrollene (kilde og import, kravidentiteter, dashbordtall) gjelder ikke en OpenSpec-katalog og er erstattet av OpenSpec-avstemmingen.");
            Done("OpenSpec-avstemming");
        }

        List<IdentityDryRunRow> identities = [];
        if (!openSpecCatalog && categories.Contains(DataQualityCategory.SourceImport))
        {
            ct.ThrowIfCancellationRequested();
            Phase("Kilde og import");
            checks.AddRange(SourceChecks(snapshot, expected));
            Done("Kilde og import");
        }
        if (!openSpecCatalog && categories.Contains(DataQualityCategory.RequirementIdentity))
        {
            ct.ThrowIfCancellationRequested();
            Phase("Kravidentiteter (tørrkjøring, skriver ingenting)");
            var dry = IdentityDryRun.Run(snapshot, expected);
            identities = dry.Rows;
            checks.AddRange(IdentityChecks(dry, expected));
            Done("Kravidentiteter");
        }
        List<DataQualityKpiExpectation> kpis = [];
        if (!openSpecCatalog && categories.Contains(DataQualityCategory.Dashboard))
        {
            ct.ThrowIfCancellationRequested();
            Phase("Dashbord: lagrede data → API");
            ManualTestingOverview? api = null;
            try { api = await overview(projectKey, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(Check("dashboard.api", DataQualityCategory.Dashboard, DataQualityStage.StoredToApi, "Oversikts-API", "Prosjekt", "", null, null, DataQualityStatus.NotVerified,
                    DataQualityCause.None, $"Oversikts-API-et kunne ikke leses ({ex.GetType().Name}); dashbordtallene er ikke kontrollert."));
            }
            if (api is not null)
            {
                checks.AddRange(DashboardChecks(expected, api));
                kpis = Kpis(expected, api);
            }
            else skipped.Add("Dashbord: API → UI ble ikke kontrollert fordi oversikts-API-et ikke svarte.");
            Done("Dashbord");
        }

        var after = await marker(projectKey, ct);
        var consistent = after == snapshot.Marker;
        if (!consistent)
        {
            checks = [.. checks.Select(c => c.Status == DataQualityStatus.Pass ? c with { Status = DataQualityStatus.Inconclusive, Cause = DataQualityCause.ConcurrentChange,
                Explanation = c.Explanation + " Dataene ble endret mens kontrollen kjørte; resultatet kan ikke regnes som bestått." } : c)];
            followUps.Add("Kjør kontrollen på nytt når ingen endrer prosjektet.");
        }
        if (!categories.Contains(DataQualityCategory.RequirementIdentity)) skipped.Add("Kravidentiteter ble ikke valgt.");
        if (!categories.Contains(DataQualityCategory.SourceImport)) skipped.Add("Kilde og import ble ikke valgt.");
        if (!categories.Contains(DataQualityCategory.Dashboard)) skipped.Add("Dashbord-tall ble ikke valgt.");
        followUps.AddRange(FollowUps(checks));
        return new DataQualityReport
        {
            RunId = runId, ProjectKey = projectKey, Baseline = baseline, SnapshotMarker = Sha(snapshot.Marker), Consistent = consistent, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow,
            Categories = [.. categories], Checks = checks, Identities = identities, Kpis = kpis, Skipped = skipped, FollowUps = followUps,
        };
    }

    // ---- OpenSpec ----

    /// <summary>The OpenSpec reconciliation's checks as Data Quality checks (status mapped one to one; NOT VERIFIED stays NOT VERIFIED).</summary>
    internal static IEnumerable<DataQualityCheck> OpenSpecChecks(BirkNext.Specifications.OpenSpecReconciliationReport report, string projectKey)
    {
        yield return Check("openspec.format", DataQualityCategory.SourceImport, DataQualityStage.SourceToStored, "Spesifikasjonsformat", projectKey,
            "Katalogen er produsert av OpenSpec-ruten (fingeravtrykk «os1-…»); gjeldende grunnlag = openspec/specs.", "OpenSpec", "OpenSpec",
            DataQualityStatus.Pass, DataQualityCause.None, $"OpenSpec-katalog, revisjon {report.CatalogRevision?.ToString() ?? "–"}. Forslag og arkiv er telt separat.");
        foreach (var c in report.Checks)
        {
            var (category, stage) = c.Id switch
            {
                var id when id.StartsWith("D04") => (DataQualityCategory.RequirementIdentity, DataQualityStage.Identity),
                var id when id.StartsWith("D07") || id.StartsWith("D08") => (DataQualityCategory.Dashboard, DataQualityStage.StoredToApi),
                _ => (DataQualityCategory.SourceImport, DataQualityStage.SourceToStored),
            };
            var status = c.Status switch
            {
                BirkNext.Specifications.ReconciliationStatus.Pass => DataQualityStatus.Pass,
                BirkNext.Specifications.ReconciliationStatus.Fail => DataQualityStatus.Fail,
                _ => DataQualityStatus.NotVerified,
            };
            var cause = status switch
            {
                DataQualityStatus.Pass => DataQualityCause.None,
                DataQualityStatus.Fail => category == DataQualityCategory.RequirementIdentity ? DataQualityCause.AmbiguousIdentity
                    : category == DataQualityCategory.Dashboard ? DataQualityCause.IncorrectAggregation : DataQualityCause.IncorrectImport,
                _ => c.Id == "S00" ? DataQualityCause.UnsupportedSource : DataQualityCause.SourceUnavailable,
            };
            yield return Check("openspec." + c.Id, category, stage, c.Title, projectKey, c.Detail, c.Expected, c.Observed, status, cause, c.Detail);
        }
    }

    // ---- baseline ----

    private static DataQualityBaseline Baseline(DataQualitySnapshot s, R.Expected e) => e.Baseline is { } b
        ? new DataQualityBaseline
        {
            CatalogId = b.Id, CatalogProjectKey = b.ProjectKey, Revision = b.Revision, ArchiveSha256 = b.ArchiveSha256, CapturedAt = b.CapturedAt,
            Documents = DataQualitySnapshotReader.Documents(b.DocumentsJson).Count, Application = s.Application,
            Label = $"{b.ProjectKey} · revisjon {b.Revision} · {b.CapturedAt:yyyy-MM-dd HH:mm}{(b.ArchiveSha256 is { Length: >= 12 } sha ? $" · arkiv {sha[..12]}" : "")}",
        }
        : new DataQualityBaseline { Application = s.Application, Label = "Ingen spesifikasjonskatalog" };

    private static DataQualityCheck BaselineCheck(DataQualitySnapshot s, R.Expected e, DataQualityBaseline b)
    {
        if (e.Baseline is null)
            return Check("baseline", DataQualityCategory.Dashboard, DataQualityStage.SourceToStored, "Kildegrunnlag", s.ProjectKey, R.Contracts["baseline"], null, null,
                DataQualityStatus.NotVerified, DataQualityCause.SourceUnavailable, "Prosjektet har ingen spesifikasjonskatalog med lagret parse. Scenarioer kan ikke kontrolleres.");
        var newer = s.ApplicationCatalogs.Where(c => c.CapturedAt > e.Baseline.CapturedAt && c.EvidenceJson is not null).OrderByDescending(c => c.CapturedAt).ToList();
        return newer.Count == 0
            ? Check("baseline", DataQualityCategory.Dashboard, DataQualityStage.SourceToStored, "Kildegrunnlag", s.ProjectKey, R.Contracts["baseline"], b.Label, b.Label,
                DataQualityStatus.Pass, DataQualityCause.None, s.Application is null ? "Ingen applikasjon er registrert; nyere importer kan ikke sammenlignes." : "Ingen nyere import av samme applikasjon.")
            : Check("baseline", DataQualityCategory.Dashboard, DataQualityStage.SourceToStored, "Kildegrunnlag", s.ProjectKey, R.Contracts["baseline"], newer[0].ProjectKey, b.Label,
                DataQualityStatus.Warning, DataQualityCause.StaleData,
                $"Dashbordet for dette prosjektet bygger på katalogen fra {e.Baseline.CapturedAt:yyyy-MM-dd HH:mm}, men {newer.Count} nyere import(er) av samme applikasjon finnes. Velg den nyeste importen som prosjekt for å se gjeldende kilde.",
                [.. newer.Select(n => new DataQualityRecord("Nyere grunnlag", n.ProjectKey, null, $"revisjon {n.Revision} · {n.CapturedAt:yyyy-MM-dd HH:mm}"))]);
    }

    // ---- source → stored ----

    private static IEnumerable<DataQualityCheck> SourceChecks(DataQualitySnapshot s, R.Expected e)
    {
        const DataQualityCategory C = DataQualityCategory.SourceImport; const DataQualityStage St = DataQualityStage.SourceToStored;
        yield return Check("source.archive", C, St, "Opprinnelig arkiv", s.ProjectKey, "Arkivets bytes sammenlignet med importen.", null, null, DataQualityStatus.NotVerified,
            DataQualityCause.SourceUnavailable, "SOURCE UNAVAILABLE: BirkNext beholder ikke det opplastede arkivet etter importen (bare dokumentteksten per innholdshash). Arkivnivået er ikke kontrollert.");
        if (e.Baseline is null) yield break;
        var docs = DataQualitySnapshotReader.Documents(e.Baseline.DocumentsJson);
        var missing = new List<DataQualityRecord>(); var tampered = new List<DataQualityRecord>();
        var scen = new List<DataQualityRecord>(); var req = new List<DataQualityRecord>(); var reqOnly = new List<DataQualityRecord>(); var outside = new List<DataQualityRecord>(); var withdrawn = new List<DataQualityRecord>();
        int compared = 0, refScenarios = 0, storedScenarios = 0;
        foreach (var (path, sha) in docs)
        {
            var spec = e.Specs.FirstOrDefault(x => x.Path == path.Replace('\\', '/'));
            if (spec is null) continue; // a supporting document without a stored parse (plan.md, research.md …)
            if (!s.Bodies.TryGetValue(sha, out var text)) { missing.Add(new("Kildetekst mangler", path, path, $"sha256 {Short(sha)} finnes ikke i dokumentlageret")); continue; }
            if (!string.Equals(Sha(text), sha, StringComparison.OrdinalIgnoreCase)) { tampered.Add(new("Hash avviker", path, path, $"lagret {Short(sha)}, innhold {Short(Sha(text))}")); continue; }
            compared++;
            var reading = ReferenceSpecParser.Read(text);
            refScenarios += reading.Scenarios.Count; storedScenarios += spec.Acceptance.Count;
            if (reading.Scenarios.Count != spec.Acceptance.Count)
                scen.Add(new("Avvik", path, path, $"referanse {reading.Scenarios.Count}, lagret {spec.Acceptance.Count}"));
            var storedReq = e.Specs.First(x => x.Path == spec.Path);
            var refIds = reading.Requirements.ToHashSet(StringComparer.Ordinal);
            var storedIds = StoredRequirementIds(e.Baseline.EvidenceJson!, spec.Path).Select(ReferenceSpecParser.NormalizeRequirementId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in refIds.Except(storedIds)) req.Add(new("Mangler i lagret parse", $"{path}#{id}", path, id));
            foreach (var id in storedIds.Except(refIds)) req.Add(new("Bare i lagret parse", $"{path}#{id}", path, id));
            var storedOnly = storedReq.RequirementOnly.Select(ReferenceSpecParser.NormalizeRequirementId).ToHashSet(StringComparer.Ordinal);
            if (!storedOnly.SetEquals(reading.RequirementsWithoutScenario))
                reqOnly.Add(new("Avvik", path, path, $"referanse {reading.RequirementsWithoutScenario.Count}, lagret {storedOnly.Count}"));
            if (reading.GivenOutsideAcceptance > 0) outside.Add(new("Given utenfor akseptansescenarioer", path, path, $"{reading.GivenOutsideAcceptance} linje(r) — telles ikke som scenarioer"));
            if (reading.WithdrawnScenarios > 0) withdrawn.Add(new("Trukket tilbake", path, path, $"{reading.WithdrawnScenarios} scenario(er) under fjernet brukerhistorie"));
        }
        var specDocs = docs.Count(d => e.Specs.Any(x => x.Path == d.Path.Replace('\\', '/')));
        yield return missing.Count == 0
            ? Check("source.text", C, St, "Kildetekst tilgjengelig", s.ProjectKey, "Hver spesifikasjon i grunnlaget har lagret tekst med samme SHA-256 som katalogen oppgir.", specDocs.ToString(), compared.ToString(),
                tampered.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, tampered.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectImport,
                tampered.Count == 0 ? "All kildetekst finnes og stemmer med katalogens hash." : "Lagret tekst stemmer ikke med katalogens hash.", tampered)
            : Check("source.text", C, St, "Kildetekst tilgjengelig", s.ProjectKey, "Hver spesifikasjon i grunnlaget har lagret tekst med samme SHA-256 som katalogen oppgir.", specDocs.ToString(), compared.ToString(),
                DataQualityStatus.NotVerified, DataQualityCause.SourceUnavailable, $"SOURCE UNAVAILABLE for {missing.Count} dokument(er): de er ikke kontrollert mot importen.", [.. missing, .. tampered]);
        if (compared == 0) yield break;
        const string ruleNote = " Referanseleseren har egne, enklere regler; et avvik vises med begge tall og er ikke i seg selv bevis på feil import.";
        yield return Check("source.scenarios", C, St, "Akseptansescenarioer i kilden", $"{compared} dokument(er)", "Nummererte punkter med Given i en «Acceptance Scenarios»-blokk under en brukerhistorie (referanseregel).",
            refScenarios.ToString(), storedScenarios.ToString(), scen.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning,
            scen.Count == 0 ? DataQualityCause.None : DataQualityCause.ParserRuleDifference, (scen.Count == 0 ? "Referanseleseren finner like mange scenarioer som importen." : $"{scen.Count} dokument(er) avviker.") + ruleNote, scen);
        yield return Check("source.requirements", C, St, "Krav-ID-er i kilden", $"{compared} dokument(er)", "Listepunkter som starter med fet krav-ID (- **FR-001**), normalisert.", null, null,
            req.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, req.Count == 0 ? DataQualityCause.None : DataQualityCause.ParserRuleDifference,
            (req.Count == 0 ? "Samme krav-ID-er i kilden og i lagret parse." : $"{req.Count} krav-ID-er finnes bare i den ene lesningen.") + ruleNote, req);
        yield return Check("source.requirementOnly", C, St, "Krav uten scenario", $"{compared} dokument(er)", R.Contracts["spec.requirementOnly"], null, null,
            reqOnly.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, reqOnly.Count == 0 ? DataQualityCause.None : DataQualityCause.ParserRuleDifference,
            (reqOnly.Count == 0 ? "Samme krav uten scenario i begge lesninger." : $"{reqOnly.Count} dokument(er) avviker.") + ruleNote, reqOnly);
        yield return Check("source.givenOutside", C, St, "Given-markører utenfor akseptansescenarioer", $"{compared} dokument(er)", "Given-linjer i Edge Cases, notater o.l. er ikke scenarioer.",
            outside.Count.ToString(), null, DataQualityStatus.Pass, DataQualityCause.None,
            outside.Count == 0 ? "Ingen Given-markører utenfor akseptanseblokker." : "Disse linjene telles ikke som scenarioer (informasjon, ikke avvik).", [.. outside, .. withdrawn]);
    }

    private static List<string> StoredRequirementIds(string evidenceJson, string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(evidenceJson);
        foreach (var item in doc.RootElement.EnumerateArray())
            if ((DataQualitySnapshotReader.Str(item, "path") ?? "").Replace('\\', '/') == path && item.TryGetProperty("evidence", out var ev))
                return [.. R.Array(ev, "requirements").Select(r => DataQualitySnapshotReader.Str(r, "id")).OfType<string>().Distinct(StringComparer.Ordinal)];
        return [];
    }

    // ---- identity ----

    private static IEnumerable<DataQualityCheck> IdentityChecks(IdentityDryRun.Result dry, R.Expected e)
    {
        const DataQualityCategory C = DataQualityCategory.RequirementIdentity; const DataQualityStage St = DataQualityStage.Identity;
        yield return Check("identity.displayIds", C, St, "Samme visnings-ID i flere spesifikasjoner", "Grunnlaget", "Visnings-ID (FR-001 …) er aldri identitet; kravet identifiseres med spesifikasjon + ID.",
            dry.RepeatedDisplayIds.Count.ToString(), null, DataQualityStatus.Pass, DataQualityCause.None,
            dry.RepeatedDisplayIds.Count == 0 ? "Ingen krav-ID gjentas." : $"{dry.RepeatedDisplayIds.Count} krav-ID-er finnes i flere spesifikasjoner (informasjon; de holdes adskilt av stien).",
            [.. dry.RepeatedDisplayIds.Select(kv => new DataQualityRecord("Gjentatt ID", kv.Key, null, string.Join(", ", kv.Value)))]);
        yield return Check("identity.sharedV1", C, St, "Delte v1-kildeidentiteter", "Grunnlaget", "v1 = hash(«requirement:{modul}|{id}») uten filsti: krav med samme ID i flere dokumenter av samme modul får én identitet.",
            dry.SharedV1.Count.ToString(), "0", dry.SharedV1.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, dry.SharedV1.Count == 0 ? DataQualityCause.None : DataQualityCause.AmbiguousIdentity,
            dry.SharedV1.Count == 0 ? "Ingen v1-identitet deles av flere dokumenter." : $"{dry.SharedV1.Count} v1-identiteter deles av flere dokumenter (opptil {dry.SharedV1.Values.Max(v => v.Count)}). v2 ville skille dem.",
            [.. dry.SharedV1.Select(kv => new DataQualityRecord("Delt v1", kv.Key, null, string.Join(", ", kv.Value)))]);
        var ambiguous = dry.Rows.Where(r => r.Resolution == IdentityResolution.Ambiguous).ToList();
        yield return Check("identity.ambiguous", C, St, "Tvetydige relasjoner", "Lagrede testtilfeller, planposter, vurderinger, gjennomføringer",
            "En relasjon til en delt v1-identitet avgjøres bare av relasjonens egen spesifikasjonssti; uten sti er den tvetydig og løses aldri automatisk.",
            "0", ambiguous.Count.ToString(), ambiguous.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, ambiguous.Count == 0 ? DataQualityCause.None : DataQualityCause.AmbiguousIdentity,
            ambiguous.Count == 0 ? "Ingen lagret relasjon er tvetydig." : $"{ambiguous.Count} krav har relasjoner som ikke kan avgjøres uten en person. De vises ikke som verifisert dekning.",
            [.. ambiguous.Select(r => new DataQualityRecord("Må vurderes", r.V1, r.SpecificationPath, $"{r.RequirementId}: {r.TestCases.Count} testtilfelle(r), {r.PlanItems.Count} planpost(er), {r.Executions.Count} gjennomføring(er)"))]);
        var unresolved = dry.Rows.Where(r => r.Resolution == IdentityResolution.Unresolved).ToList();
        yield return Check("identity.unresolved", C, St, "Relasjoner uten krav i grunnlaget", "Lagrede relasjoner", "En lagret relasjon til en kravidentitet som ingen spesifikasjon i grunnlaget har.",
            "0", unresolved.Count.ToString(), unresolved.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, unresolved.Count == 0 ? DataQualityCause.None : DataQualityCause.AmbiguousIdentity,
            unresolved.Count == 0 ? "Alle lagrede kravrelasjoner finner kravet i grunnlaget." : $"{unresolved.Count} v1-identitet(er) er ikke i grunnlaget.",
            [.. unresolved.Select(r => new DataQualityRecord("Uløst", r.V1, r.SpecificationPath, string.Join("; ", r.TestCases.Concat(r.PlanItems).Take(5))))]);
        yield return Check("identity.v1Variants", C, St, "Samme krav med flere v1-identiteter", "Grunnlaget og lagrede oppdagelser",
            "v1 inneholder modulvalget da kravet ble lest: samme krav kan få ulik identitet i import og i nettleseren.", null, dry.V1Variants.Count.ToString(),
            dry.V1Variants.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, dry.V1Variants.Count == 0 ? DataQualityCause.None : DataQualityCause.AmbiguousIdentity,
            dry.V1Variants.Count == 0 ? "Ingen krav er sett under mer enn én v1-identitet." : $"{dry.V1Variants.Count} krav er sett under flere v1-identiteter.",
            [.. dry.V1Variants.Select(v => new DataQualityRecord("v1-variant", v))]);
        yield return Check("identity.relocations", C, St, "Mulig flyttede krav", "Forrige og gjeldende katalogrevisjon", "Samme ID og samme kravtekst under en annen sti. Kobles aldri automatisk.",
            null, dry.Relocations.Count.ToString(), dry.Relocations.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, dry.Relocations.Count == 0 ? DataQualityCause.None : DataQualityCause.AmbiguousIdentity,
            dry.Relocations.Count == 0 ? "Ingen krav ser ut til å være flyttet mellom revisjonene." : "Disse kravene ser ut til å være flyttet; v2 ville gi dem ny identitet, så de må bekreftes av en person.",
            [.. dry.Relocations.Select(v => new DataQualityRecord("Flyttet?", v))]);
        var pathless = e.Cases.Where(c => c.Identity is not null && string.IsNullOrWhiteSpace(c.SpecificationPath)).ToList();
        yield return Check("identity.coveredKeys", C, St, "Dekningsnøkler uten spesifikasjon", "Aktive testtilfeller", R.Contracts["metric.covered"], "0", pathless.Count.ToString(),
            pathless.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, pathless.Count == 0 ? DataQualityCause.None : DataQualityCause.AmbiguousIdentity,
            pathless.Count == 0 ? "Alle testtilfeller med kildeidentitet har en spesifikasjonssti." : $"{pathless.Count} testtilfelle(r) har ingen spesifikasjonssti: de dekker ingen rad (underteller, overteller aldri).",
            [.. pathless.Select(c => new DataQualityRecord("Uten sti", c.CaseId, null, $"{c.PlanName} · {c.Identity}"))]);
        yield return Check("identity.identity1", C, St, "identity/1-bekreftelser (kanonisk tolkning)", "Prosjektet", "En bekreftet identity/1 gjelder én prosjektbred krav-ID og dermed alle dokumenter med den ID-en.",
            null, null, DataQualityStatus.NotVerified, DataQualityCause.None, "Ikke kontrollert i denne versjonen: identity/1-beslutninger ligger i den kanoniske tolkningens lager og leses ikke her.");
    }

    // ---- stored → API ----

    private static IEnumerable<DataQualityCheck> DashboardChecks(R.Expected e, ManualTestingOverview api)
    {
        const DataQualityCategory C = DataQualityCategory.Dashboard; const DataQualityStage St = DataQualityStage.StoredToApi;
        var apiSpecs = api.Specifications.Where(x => !x.Scenarios.All(sc => sc.Standalone) || x.Scenarios.Count == 0).ToDictionary(x => x.Path.Replace('\\', '/'), x => x, StringComparer.Ordinal);
        var missingSpecs = e.Specs.Where(x => !apiSpecs.ContainsKey(x.Path)).Select(x => new DataQualityRecord("Mangler i API", x.Path, x.Path)).ToList();
        var extraSpecs = apiSpecs.Keys.Where(p => e.Specs.All(x => x.Path != p)).Select(p => new DataQualityRecord("Bare i API", p, p)).ToList();
        yield return Check("dashboard.specifications", C, St, "Spesifikasjoner", "Prosjekt", R.Contracts["baseline"], e.Specs.Count.ToString(), apiSpecs.Count.ToString(),
            missingSpecs.Count + extraSpecs.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, missingSpecs.Count + extraSpecs.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            missingSpecs.Count + extraSpecs.Count == 0 ? "Samme spesifikasjoner i referansen og API-et." : "Spesifikasjonene i API-et avviker fra katalogen.", [.. missingSpecs, .. extraSpecs]);

        var acc = new List<DataQualityRecord>(); var reqs = new List<DataQualityRecord>(); var dup = new List<DataQualityRecord>(); var modules = new List<DataQualityRecord>();
        int refAcc = 0, apiAcc = 0, refReq = 0, apiReq = 0;
        foreach (var spec in e.Specs)
        {
            refAcc += spec.Acceptance.Count; refReq += spec.RequirementOnly.Count;
            foreach (var d in spec.DuplicateKeys) dup.Add(new("Duplikat-identitet", $"{spec.Path}#{d}", spec.Path, "to lagrede scenarioer har samme historie og nummer"));
            if (!apiSpecs.TryGetValue(spec.Path, out var a)) continue;
            // Scenarios under a user story are compared by story + number; scenarios without one (e.g. «Scenario N:» headings) have no shared key
            // between the stored parse and the API, so they are compared by count only.
            var apiAccAll = a.Scenarios.Where(sc => !sc.RequirementOnly && !sc.Standalone).ToList();
            apiAcc += apiAccAll.Count;
            var apiAccKeys = apiAccAll.Where(sc => sc.StoryKey is not null && sc.Number is not null).Select(sc => $"{sc.StoryKey}#{sc.Number}").ToList();
            var refKeys = spec.Acceptance.Where(x => x.StoryKey is not null && x.Number is not null).Select(x => x.Key).ToList();
            var refLoose = spec.Acceptance.Count - refKeys.Count; var apiLoose = apiAccAll.Count - apiAccKeys.Count;
            if (refLoose != apiLoose) acc.Add(new("Antall uten brukerhistorie", spec.Path, spec.Path, $"referanse {refLoose}, API {apiLoose}"));
            foreach (var k in refKeys.Except(apiAccKeys, StringComparer.Ordinal)) acc.Add(new("Mangler i API", $"{spec.Path}#{k}", spec.Path, spec.Acceptance.First(x => x.Key == k).Title));
            foreach (var k in apiAccKeys.Except(refKeys, StringComparer.Ordinal)) acc.Add(new("Bare i API", $"{spec.Path}#{k}", spec.Path));
            if (refKeys.Count != apiAccKeys.Count && refKeys.ToHashSet().SetEquals(apiAccKeys)) acc.Add(new("Antall avviker", spec.Path, spec.Path, $"referanse {refKeys.Count}, API {apiAccKeys.Count} (samme nøkler)"));
            var apiReqIds = a.Scenarios.Where(sc => sc.RequirementOnly).Select(sc => sc.Title.Split(':')[0].Trim()).ToList();
            apiReq += apiReqIds.Count;
            foreach (var id in spec.RequirementOnly.Except(apiReqIds, StringComparer.Ordinal)) reqs.Add(new("Mangler i API", $"{spec.Path}#{id}", spec.Path, id));
            foreach (var id in apiReqIds.Except(spec.RequirementOnly, StringComparer.Ordinal)) reqs.Add(new("Bare i API", $"{spec.Path}#{id}", spec.Path, id));
            if (!string.Equals(a.Module, spec.Module, StringComparison.Ordinal)) modules.Add(new("Ulik modul", spec.Path, spec.Path, $"referanse «{spec.Module}», API «{a.Module}»"));
        }
        var accStatus = acc.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail;
        yield return Check("dashboard.acceptance", C, St, "Akseptansescenarioer", "Prosjekt", R.Contracts["spec.acceptance"], refAcc.ToString(), apiAcc.ToString(), accStatus,
            acc.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            acc.Count == 0 ? "Hvert lagret akseptansescenario er i API-et, og ingen andre." : $"{acc.Count} avvik mellom lagrede scenarioer og API-et.", [.. acc, .. dup]);
        yield return Check("dashboard.requirementOnly", C, St, "Krav uten scenario", "Prosjekt", R.Contracts["spec.requirementOnly"], refReq.ToString(), apiReq.ToString(),
            reqs.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, reqs.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            reqs.Count == 0 ? "Samme krav uten scenario i referansen og API-et." : $"{reqs.Count} avvik.", reqs);
        yield return Check("dashboard.modules", C, St, "Modultilknytning", "Prosjekt", R.Contracts["module.rule"], null, null,
            modules.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Warning, modules.Count == 0 ? DataQualityCause.None : DataQualityCause.ParserRuleDifference,
            modules.Count == 0 ? "Referansens modulregel gir samme modul for alle spesifikasjoner." : $"{modules.Count} spesifikasjon(er) får ulik modul. Referansens regel er enklere (ingen nestede moduler); kontroller stien.", modules);

        var apiDocumented = api.Specifications.SelectMany(x => x.Scenarios.Where(sc => sc.Standalone).Select(sc => sc.Identity)).ToHashSet(StringComparer.Ordinal);
        var refDocumented = e.DocumentedTests.Select(d => d.Identity).ToHashSet(StringComparer.Ordinal);
        var docDiff = refDocumented.Except(apiDocumented).Select(i => new DataQualityRecord("Mangler i API", i, e.DocumentedTests.First(d => d.Identity == i).Path, e.DocumentedTests.First(d => d.Identity == i).Title))
            .Concat(apiDocumented.Except(refDocumented).Select(i => new DataQualityRecord("Bare i API", i))).ToList();
        yield return Check("dashboard.documented", C, St, "Dokumenterte tester", "Prosjekt", R.Contracts["documented"], refDocumented.Count.ToString(), apiDocumented.Count.ToString(),
            docDiff.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, docDiff.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            docDiff.Count == 0 ? "Samme dokumenterte tester." : $"{docDiff.Count} avvik.", docDiff);

        var apiCases = api.Cases.Select(c => (c.PlanId, c.CaseId)).ToHashSet();
        var refCases = e.Cases.Select(c => (c.PlanId, c.CaseId)).ToHashSet();
        var caseDiff = refCases.Except(apiCases).Select(c => new DataQualityRecord("Mangler i API", c.CaseId, null, e.Cases.First(x => (x.PlanId, x.CaseId) == c).PlanName))
            .Concat(apiCases.Except(refCases).Select(c => new DataQualityRecord("Bare i API", c.CaseId, null, api.Cases.First(x => (x.PlanId, x.CaseId) == c).PlanName))).ToList();
        yield return Count("dashboard.cases", "Testtilfeller", R.Contracts["metric.testCases"], refCases.Count, apiCases.Count, caseDiff);
        yield return Count("dashboard.approved", "Godkjente", R.Contracts["metric.approved"], e.Cases.Count(c => c.Review == "Accepted"), api.Cases.Count(c => c.Review == TestCaseReviewState.Accepted),
            Diff(e.Cases.Where(c => c.Review == "Accepted").Select(c => c.CaseId), api.Cases.Where(c => c.Review == TestCaseReviewState.Accepted).Select(c => c.CaseId)));
        yield return Count("dashboard.ready", "Klar til testing", R.Contracts["metric.ready"], e.Cases.Count(c => c.Ready), api.Cases.Count(c => c.Ready),
            Diff(e.Cases.Where(c => c.Ready).Select(c => c.CaseId), api.Cases.Where(c => c.Ready).Select(c => c.CaseId)));
        var linkDiff = e.Cases.Where(c => c.Identity is not null).Select(c => (c, a: api.Cases.FirstOrDefault(x => x.PlanId == c.PlanId && x.CaseId == c.CaseId)))
            .Where(t => t.a is not null && (t.a.ScenarioIdentity != t.c.Identity || !SamePath(t.a.SpecificationPath, t.c.SpecificationPath)))
            .Select(t => new DataQualityRecord("Ulik kobling", t.c.CaseId, t.c.SpecificationPath, $"referanse {t.c.SpecificationPath}|{t.c.Identity}, API {t.a!.SpecificationPath}|{t.a.ScenarioIdentity}")).ToList();
        yield return Check("dashboard.caseLinks", C, St, "Testtilfellenes scenariokobling", "Aktive testtilfeller", R.Contracts["metric.covered"], "0", linkDiff.Count.ToString(),
            linkDiff.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, linkDiff.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            linkDiff.Count == 0 ? "Hvert testtilfelle har samme spesifikasjon og kildeidentitet i referansen og API-et." : $"{linkDiff.Count} testtilfelle(r) kobles ulikt.", linkDiff);

        var planDiff = new List<DataQualityRecord>();
        var apiPlans = api.Plans.ToDictionary(p => p.Id);
        foreach (var p in e.Plans)
        {
            if (!apiPlans.TryGetValue(p.Id, out var a)) { planDiff.Add(new("Mangler i API", p.Id.ToString(), null, p.Name)); continue; }
            if (a.Suites is { } suites && suites != p.Suites) planDiff.Add(new("Testgrupper", p.Name, null, $"referanse {p.Suites}, API {suites}"));
            if (a.Execution is { } x)
            {
                if (x.Scenarios != p.Occurrences.Count) planDiff.Add(new("Forekomster", p.Name, null, $"referanse {p.Occurrences.Count}, API {x.Scenarios}"));
                if ((x.Passed, x.Failed, x.Blocked, x.NotRun) != (p.Passed, p.Failed, p.Blocked, p.NotRun))
                    planDiff.Add(new("Gjennomføring", p.Name, null, $"referanse {p.Passed}/{p.Failed}/{p.Blocked}/{p.NotRun}, API {x.Passed}/{x.Failed}/{x.Blocked}/{x.NotRun} (bestått/feilet/blokkert/ikke kjørt)"));
            }
            if (a.PlannedOnly is { } po && po != p.PlannedOnly) planDiff.Add(new("Planlagt uten testtilfelle", p.Name, null, $"referanse {p.PlannedOnly}, API {po}"));
        }
        foreach (var a in api.Plans.Where(a => e.Plans.All(p => p.Id != a.Id))) planDiff.Add(new("Bare i API", a.Id.ToString(), null, a.Name));
        yield return Check("dashboard.plans", C, St, "Testplaner, testgrupper, forekomster og gjennomføring", "Aktive testplaner",
            $"{R.Contracts["plans"]} {R.Contracts["plan.occurrences"]} {R.Contracts["plan.execution"]}", e.Plans.Count.ToString(), api.Plans.Count.ToString(),
            planDiff.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, planDiff.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            planDiff.Count == 0 ? "Planene, testgruppene, forekomstene og resultatene stemmer." : $"{planDiff.Count} avvik.", planDiff);
    }

    private static bool SamePath(string? a, string? b) => (a ?? "").Replace('\\', '/').Trim('/').Equals((b ?? "").Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);

    private static List<DataQualityRecord> Diff(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = expected.ToList(); var a = actual.ToList();
        return [.. e.Except(a, StringComparer.Ordinal).Select(x => new DataQualityRecord("Mangler i API", x)), .. a.Except(e, StringComparer.Ordinal).Select(x => new DataQualityRecord("Bare i API", x))];
    }

    private static DataQualityCheck Count(string id, string metric, string contract, int expected, int actual, List<DataQualityRecord> records) =>
        Check(id, DataQualityCategory.Dashboard, DataQualityStage.StoredToApi, metric, "Prosjekt", contract, expected.ToString(), actual.ToString(),
            expected == actual && records.Count == 0 ? DataQualityStatus.Pass : DataQualityStatus.Fail, expected == actual && records.Count == 0 ? DataQualityCause.None : DataQualityCause.IncorrectAggregation,
            expected == actual && records.Count == 0 ? "Referansen og API-et har de samme postene." : $"Differanse {actual - expected}; {records.Count} post(er) avviker.", records);

    // ---- API → UI (expected figures for the browser) ----

    /// <summary>Expected dashboard figures for the browser stage. Scenario rows come from the API (verified separately in stage 2, by path and key);
    /// coverage, cases, plans and modules come from the reference calculation.</summary>
    public static List<DataQualityKpiExpectation> Kpis(R.Expected e, ManualTestingOverview api)
    {
        var rows = api.Specifications.SelectMany(x => x.Scenarios.Select(sc => (Spec: x, Sc: sc))).ToList();
        bool Covered((OverviewSpecification Spec, OverviewScenario Sc) r) => e.CoveredKeys.Contains(R.Key(r.Spec.Path, r.Sc.Identity));
        var list = new List<DataQualityKpiExpectation>
        {
            new("metric.scenarios", "Scenarioforslag", "Prosjekt", e.Specs.Sum(x => x.Acceptance.Count + x.RequirementOnly.Count) + e.DocumentedTests.Count, R.Contracts["metric.scenarios"], []),
            new("metric.testCases", "Testtilfeller", "Prosjekt", e.Cases.Count, R.Contracts["metric.testCases"], []),
            new("metric.approved", "Godkjente", "Prosjekt", e.Cases.Count(c => c.Review == "Accepted"), R.Contracts["metric.approved"], []),
            new("metric.ready", "Klar til testing", "Prosjekt", e.Cases.Count(c => c.Ready), R.Contracts["metric.ready"], []),
            new("metric.covered", "Med testtilfeller", "Prosjekt", rows.Count(Covered), R.Contracts["metric.covered"], [.. rows.Where(Covered).Select(r => r.Sc.Identity).Take(MaxRecordsPerCheck)]),
            new("e2e.documented", "Dokumentert E2E", "Prosjekt", e.DocumentedTests.Count(d => d.DeclaredEndToEnd), R.Contracts["e2e.documented"],
                [.. e.DocumentedTests.Where(d => d.DeclaredEndToEnd).Select(d => d.Identity)]),
            new("e2e.candidates", "E2E-kandidater", "Prosjekt", e.E2ECandidates.Count, R.Contracts["e2eCandidates"], [.. e.E2ECandidates.Select(c => c.Identity).Take(MaxRecordsPerCheck)]),
            new("plans", "Testplaner", "Prosjekt", e.Plans.Count, R.Contracts["plans"], [.. e.Plans.Select(p => p.Name)]),
        };
        foreach (var p in rows.Where(r => !Covered(r)).GroupBy(r => r.Sc.Priority ?? "Ikke prioritert").OrderBy(g => g.Key, StringComparer.Ordinal))
            list.Add(new($"gaps.{p.Key}", $"Uten testtilfeller – {p.Key}", "Prosjekt", p.Count(), R.Contracts["gaps.priority"], [.. p.Select(r => r.Sc.Identity).Take(MaxRecordsPerCheck)]));
        foreach (var m in e.Specs.GroupBy(x => x.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var documented = e.DocumentedTests.Count(d => e.Specs.All(x => x.Path != d.Path) && ModuleOfPath(e, d.Path) == m.Key);
            list.Add(new($"module.scenarios.{m.Key}", $"Scenarioer – {m.Key}", m.Key, m.Sum(x => x.Acceptance.Count) + documented, R.Contracts["module.scenarios"], []));
            list.Add(new($"module.requirements.{m.Key}", $"Krav uten scenario – {m.Key}", m.Key, m.Sum(x => x.RequirementOnly.Count), R.Contracts["module.requirements"], []));
        }
        return list;
    }

    private static string ModuleOfPath(R.Expected e, string path)
    {
        var seg = path.Replace('\\', '/').Trim('/').Split('/');
        if (seg.Length > 1 && e.Specs.Any(x => x.Module == seg[0])) return seg[0];
        return seg[0].Equals("specs", StringComparison.OrdinalIgnoreCase) ? R.ProjectWide : R.Unassigned;
    }

    // ---- helpers ----

    private static IEnumerable<string> FollowUps(List<DataQualityCheck> checks)
    {
        if (checks.Any(c => c.Cause == DataQualityCause.StaleData)) yield return "Åpne den nyeste importen av applikasjonen som prosjekt i Manual Testing, og kontroller dashbordet der.";
        if (checks.Any(c => c.Cause == DataQualityCause.IncorrectAggregation)) yield return "Avvik mellom lagrede data og API-et: meld feilen med denne rapporten (JSON) vedlagt.";
        if (checks.Any(c => c.Cause == DataQualityCause.AmbiguousIdentity)) yield return "Tvetydige kravrelasjoner: gå gjennom listen «Kravidentiteter» før en eventuell identitetsmigrering besluttes. Ingenting er endret.";
        if (checks.Any(c => c.Cause == DataQualityCause.SourceUnavailable)) yield return "Kildetekst er ikke tilgjengelig for alle dokumenter: importer prosjektet på nytt i et isolert miljø for full kildekontroll.";
        if (checks.Any(c => c.Cause == DataQualityCause.ParserRuleDifference)) yield return "Referanseleseren og importen leser noen dokumenter ulikt: kontroller de oppførte dokumentene manuelt.";
    }

    internal static DataQualityCheck Check(string id, DataQualityCategory category, DataQualityStage stage, string metric, string scope, string contract, string? expected, string? actual,
        DataQualityStatus status, DataQualityCause cause, string explanation, List<DataQualityRecord>? records = null)
    {
        records ??= [];
        string? difference = int.TryParse(expected, out var x) && int.TryParse(actual, out var y) ? (y - x).ToString("+0;-0;0") : null;
        return new DataQualityCheck
        {
            Id = id, Category = category, Stage = stage, Metric = metric, Scope = scope, Contract = contract, Expected = expected, Actual = actual, Difference = difference, Status = status,
            Cause = cause, Explanation = explanation, Records = [.. records.Take(MaxRecordsPerCheck)], RecordsOmitted = Math.Max(0, records.Count - MaxRecordsPerCheck),
        };
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Short(string sha) => sha.Length >= 12 ? sha[..12] : sha;
}
