using System.Text.Json;

namespace BirkNext.Api.Services.DataQuality;

/// <summary>
/// The INDEPENDENT reference calculation of the dashboard's inputs (2026-11-27): it reads the stored JSON of a <see cref="DataQualitySnapshot"/>
/// with its own code (JsonDocument, its own module rule, its own occurrence rule) and never calls the production aggregation it audits
/// (<c>TestPlanSuggestionService.OverviewAsync</c>, <c>PlanOccurrences</c>, <c>ModuleStructure</c>, <c>ManualTestingOverviewPresentation</c>).
/// Each rule implements the written KPI contract in <see cref="Contracts"/>.
/// </summary>
public static class ReferenceCalculator
{
    public const string ProjectWide = "Prosjektnivå", Unassigned = "Uten modultilknytning";

    public sealed record SpecScenario(string Key, string? StoryKey, int? Number, string Title, string? Priority);
    public sealed record Spec(string Path, string Module, List<SpecScenario> Acceptance, List<string> RequirementOnly, List<string> DuplicateKeys);
    public sealed record DocumentedTest(string Identity, string Path, bool DeclaredEndToEnd, string Title);
    public sealed record Candidate(string Identity, string Capability, string Title);
    public sealed record Case(Guid PlanId, string PlanName, string CaseId, string? Identity, string? SpecificationPath, string Review, bool Ready, bool Manual);
    public sealed record Plan(Guid Id, string Name, int Suites, List<string> Occurrences, int PlannedOnly, int Passed, int Failed, int Blocked, int NotRun, bool OwnProject);
    public sealed record Expected(DataQualitySnapshot.CatalogRow? Baseline, List<Spec> Specs, List<DocumentedTest> DocumentedTests, List<Candidate> E2ECandidates,
        List<Case> Cases, List<Plan> Plans, List<string> Notes)
    {
        /// <summary>«path|identity» of every active case's scenario (the case's own specification path: a hand-written case under a scenario uses that scenario's case).</summary>
        public HashSet<string> CoveredKeys { get; } = [.. Cases.Where(c => c.Identity is not null).Select(c => Key(c.SpecificationPath, c.Identity!))];
    }

    /// <summary>The written KPI contracts (counting entity, identity, scope, denominator, inclusion rules); shown in the UI and the report.</summary>
    public static readonly IReadOnlyDictionary<string, string> Contracts = new Dictionary<string, string>
    {
        ["baseline"] = "Kildegrunnlag: prosjektets nyeste spesifikasjonskatalog (høyeste revisjon med lagret parse) for prosjektnøkkelen. En nyere katalog fra en annen import av samme applikasjon betyr at dashbordet viser et eldre grunnlag.",
        ["spec.acceptance"] = "Akseptansescenarioer per spesifikasjon: hver lagret akseptansescenario-post i katalogens parse, identifisert ved brukerhistorie + nummer. To poster med samme historie og nummer er en duplikat-identitet.",
        ["spec.requirementOnly"] = "Krav uten scenario per spesifikasjon: hvert krav-ID i katalogens parse som ingen akseptansescenario i samme dokument nevner. Telles aldri som scenario.",
        ["documented"] = "Dokumenterte tester: kandidater fra testspesifikasjoner (test-specification) i prosjektets nyeste gjeldende oppdagelse, unike på kildeidentitet. Dokumentert E2E = typen BusinessE2E (nivået er oppgitt av dokumentet, ikke verifisert).",
        ["e2eCandidates"] = "E2E-kandidater: andre kandidater av typen BusinessE2E i samme oppdagelse (forslag, ikke dokumenterte tester). Telles ikke i E2E-flisen.",
        ["metric.scenarios"] = "Scenarioforslag: alle rader i Testoversikt — akseptansescenarioer + krav uten scenario + dokumenterte tester, unike per spesifikasjon.",
        ["metric.testCases"] = "Testtilfeller: aktive testtilfeller (ikke Ekskludert/Erstattet) i prosjektets aktive testplaner og aktive testplaner fra andre importer av samme applikasjon.",
        ["metric.approved"] = "Godkjente: aktive testtilfeller med vurdering «Accepted».",
        ["metric.ready"] = "Klar til testing: aktive testtilfeller der lagret testbarhet sier klar. Godkjent er ikke det samme som klar.",
        ["metric.covered"] = "Med testtilfeller: rader (spesifikasjon + kildeidentitet) som minst ett aktivt testtilfelle har samme spesifikasjonssti og kildeidentitet som. Aldri på visnings-ID alene.",
        ["module.scenarios"] = "Scenarioer per modul: akseptansescenarioer + dokumenterte tester i modulens dokumenter (beslutning 2026-11-27: krav uten scenario er egen kolonne).",
        ["module.requirements"] = "Krav uten scenario per modul.",
        ["module.rule"] = "Referansens modulregel: første mappe i stien er modul når prosjektet har en <mappe>/specs/-struktur; specs/ på rot = Prosjektnivå; ellers Uten modultilknytning.",
        ["e2e.documented"] = "E2E-flisen: dokumenterte tester med typen BusinessE2E, unike på kildeidentitet, i hele prosjektet.",
        ["plans"] = "Testplaner: aktive planer (prosjektets egne + aktive planer fra andre importer av samme applikasjon).",
        ["plan.occurrences"] = "Forekomster per plan: hvert scenario én gang (planlagt post eller testtilfellenes scenario), hvert håndskrevne testtilfelle uten scenario for seg. Ekskluderte/erstattede testtilfeller teller ikke.",
        ["plan.execution"] = "Gjennomføring per plan: nyeste registrerte resultat per forekomst (ManualTestRuns, «occ:»-nøkler); uten resultat = ikke kjørt.",
        ["gaps.priority"] = "Uten testtilfeller per prioritet: rader uten testtilfelle gruppert på historiens prioritet (P1 … P7); rader uten prioritet = Ikke prioritert.",
    };

    public static string Key(string? path, string identity) => $"{(path ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant()}|{identity}";

    public static Expected Calculate(DataQualitySnapshot s)
    {
        var notes = new List<string>();
        var baseline = s.Catalogs.Where(c => c.EvidenceJson is not null).OrderByDescending(c => c.Revision).FirstOrDefault(c => EvidenceCount(c.EvidenceJson!) > 0);
        if (baseline is null) notes.Add("Ingen spesifikasjonskatalog med lagret parse for prosjektet: scenarioer kan ikke kontrolleres.");
        var specs = baseline is null ? [] : Specs(baseline.EvidenceJson!);
        var (documented, candidates) = Discovery(s);
        // The reference module rule (own implementation): roots are first folders with a specs/ folder below them.
        var paths = specs.Select(x => x.Path).Concat(documented.Select(d => d.Path)).ToList();
        var roots = paths.Select(p => p.Split('/')).Where(seg => seg.Length > 2 && seg[1].Equals("specs", StringComparison.OrdinalIgnoreCase)
            && !seg[0].Equals("specs", StringComparison.OrdinalIgnoreCase) && !seg[0].Equals("docs", StringComparison.OrdinalIgnoreCase)).Select(seg => seg[0]).ToHashSet(StringComparer.Ordinal);
        string Module(string path)
        {
            var seg = path.Replace('\\', '/').Trim('/').Split('/');
            if (seg.Length > 1 && roots.Contains(seg[0])) return seg[0];
            return seg[0].Equals("specs", StringComparison.OrdinalIgnoreCase) ? ProjectWide : Unassigned;
        }
        specs = [.. specs.Select(x => x with { Module = Module(x.Path) })];
        var (cases, plans) = Plans(s);
        return new Expected(baseline, specs, documented, candidates, cases, plans, notes);
    }

    public static string ModuleOf(Expected e, string path) => e.Specs.FirstOrDefault(x => x.Path == path)?.Module ?? Unassigned;

    private static int EvidenceCount(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.GetArrayLength() : 0; }
        catch (JsonException) { return 0; }
    }

    private static List<Spec> Specs(string evidenceJson)
    {
        var result = new List<Spec>();
        using var doc = JsonDocument.Parse(evidenceJson);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var path = (DataQualitySnapshotReader.Str(item, "path") ?? "").Replace('\\', '/');
            if (!item.TryGetProperty("evidence", out var ev)) continue;
            var priorities = Array(ev, "stories").ToDictionary(st => DataQualitySnapshotReader.Str(st, "key") ?? "", st => NormalPriority(DataQualitySnapshotReader.Str(st, "priority")), StringComparer.Ordinal);
            var acceptance = new List<SpecScenario>();
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var sc in Array(ev, "acceptanceScenarios"))
            {
                i++;
                var story = DataQualitySnapshotReader.Str(sc, "storyKey");
                var number = int.TryParse(DataQualitySnapshotReader.Str(sc, "numberInStory"), out var n) ? n : (int?)null;
                var key = story is not null && number is not null ? $"{story}#{number}" : $"#{DataQualitySnapshotReader.Str(sc, "id") ?? i.ToString()}";
                acceptance.Add(new SpecScenario(key, story, number, DataQualitySnapshotReader.Str(sc, "title") ?? DataQualitySnapshotReader.Str(sc, "when") ?? key,
                    story is null ? null : priorities.GetValueOrDefault(story)));
                foreach (var r in Array(sc, "requirementIds")) if (r.ValueKind == JsonValueKind.String) named.Add(r.GetString()!);
            }
            var requirementOnly = Array(ev, "requirements").Select(r => DataQualitySnapshotReader.Str(r, "id")).OfType<string>().Where(id => id.Trim().Length > 0)
                .Distinct(StringComparer.Ordinal).Where(id => !named.Contains(id)).ToList();
            var duplicates = acceptance.GroupBy(a => a.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            result.Add(new Spec(path, "", acceptance, requirementOnly, duplicates));
        }
        return result;
    }

    private static (List<DocumentedTest>, List<Candidate>) Discovery(DataQualitySnapshot s)
    {
        var current = s.Discoveries.Where(d => d.BasisKind is null or "Current" or "DocumentsOnly").OrderByDescending(d => d.DiscoveredAt).FirstOrDefault();
        if (current is null) return ([], []);
        var documented = new List<DocumentedTest>(); var candidates = new List<Candidate>();
        try
        {
            using var doc = JsonDocument.Parse(current.ContentJson);
            foreach (var c in Array(doc.RootElement, "candidates"))
            {
                var capability = DataQualitySnapshotReader.Str(c, "capabilityId") ?? "";
                var identity = DataQualitySnapshotReader.Str(c, "sourceIdentity") ?? "";
                var type = DataQualitySnapshotReader.Str(c, "type");
                var key = DataQualitySnapshotReader.Str(c, "canonicalKey") ?? "";
                var title = DataQualitySnapshotReader.Str(c, "title") ?? identity;
                if (capability == "test-specification" && identity.Length > 0 && key.StartsWith("testspec:", StringComparison.Ordinal))
                    documented.Add(new DocumentedTest(identity, key["testspec:".Length..].Split('#')[0], type == "BusinessE2E", title));
                else if (type == "BusinessE2E" && identity.Length > 0) candidates.Add(new Candidate(identity, capability, title));
            }
        }
        catch (JsonException) { }
        return ([.. documented.DistinctBy(d => d.Identity, StringComparer.Ordinal)], [.. candidates.DistinctBy(c => c.Identity, StringComparer.Ordinal)]);
    }

    private static (List<Case>, List<Plan>) Plans(DataQualitySnapshot s)
    {
        var cases = new List<Case>(); var plans = new List<Plan>();
        var latestRun = s.Runs.Where(r => r.CaseId.StartsWith("occ:", StringComparison.Ordinal))
            .GroupBy(r => (r.DraftId, Key: r.CaseId[4..])).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CompletedAt ?? r.StartedAt).ThenByDescending(r => r.Id).First().Outcome);
        foreach (var row in s.Drafts.Where(d => d.Lifecycle == "Active"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(row.ContentJson); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var name = DataQualitySnapshotReader.Str(root, "planName") ?? "";
                var own = DataQualitySnapshotReader.Str(root, "projectKey") == s.ProjectKey;
                var suiteIds = Array(root, "suites").Select(x => DataQualitySnapshotReader.Str(x, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var all = Array(root, "cases").ToList();
                var active = all.Where(c => DataQualitySnapshotReader.Str(c, "review") is not ("Excluded" or "Superseded")).ToList();
                // A discovered case is its own scenario's case; a hand-written case written under a scenario (scenarioKey) belongs to that scenario's case.
                var byScenario = all.Where(c => Source(c, "kind") != "Manual").GroupBy(c => Source(c, "scenarioId") ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var occurrences = new List<string>();
                foreach (var c in active)
                {
                    var manual = Source(c, "kind") == "Manual";
                    var scenarioKey = DataQualitySnapshotReader.Str(c, "scenarioKey");
                    var parent = manual && scenarioKey is not null && byScenario.TryGetValue(scenarioKey, out var p) ? p : c;
                    var generated = parent.TryGetProperty("generated", out var g) ? g : default;
                    var identity = generated.ValueKind == JsonValueKind.Object ? DataQualitySnapshotReader.Str(generated, "sourceIdentity") : null;
                    cases.Add(new Case(row.Id, name, Source(c, "scenarioId") ?? "", identity, SpecPathOf(generated), DataQualitySnapshotReader.Str(c, "review") ?? "",
                        c.TryGetProperty("testability", out var t) && DataQualitySnapshotReader.Str(t, "ready") is "true" or "True", manual));
                    var occurrence = manual ? scenarioKey is null ? "case:" + Source(c, "scenarioId") : "scenario:" + scenarioKey : "scenario:" + Source(c, "scenarioId");
                    if (!occurrences.Contains(occurrence)) occurrences.Add(occurrence);
                }
                var plannedOnly = 0;
                foreach (var planned in Array(root, "plannedScenarios"))
                {
                    var key = "scenario:" + DataQualitySnapshotReader.Str(planned, "scenarioId");
                    if (occurrences.Contains(key)) continue;
                    occurrences.Add(key); plannedOnly++;
                }
                int Count(string outcome) => occurrences.Count(o => latestRun.TryGetValue((row.Id, o), out var x) && x == outcome);
                var passed = Count("Passed"); var failed = Count("Failed"); var blocked = Count("Blocked");
                plans.Add(new Plan(row.Id, name, suiteIds.Count, occurrences, plannedOnly, passed, failed, blocked, occurrences.Count - passed - failed - blocked, own));
            }
        }
        return (cases, plans);
    }

    private static string? Source(JsonElement c, string field) => c.TryGetProperty("source", out var s) ? DataQualitySnapshotReader.Str(s, field) : null;

    /// <summary>A case's specification: its story's specification path, else the file of its first evidence reference ("path#id").</summary>
    private static string? SpecPathOf(JsonElement generated)
    {
        if (generated.ValueKind != JsonValueKind.Object) return null;
        if (generated.TryGetProperty("story", out var st) && DataQualitySnapshotReader.Str(st, "specificationPath") is { Length: > 0 } p) return p;
        foreach (var e in Array(generated, "evidence"))
            if (DataQualitySnapshotReader.Str(e, "reference") is { } r && r.IndexOf('#') is var h and > 0) return r[..h];
        return null;
    }

    internal static IEnumerable<JsonElement> Array(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in p.Value.EnumerateArray()) yield return x;
                yield break;
            }
    }

    public static string? NormalPriority(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(p, @"P\s*(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? "P" + m.Groups[1].Value : null;
    }
}
