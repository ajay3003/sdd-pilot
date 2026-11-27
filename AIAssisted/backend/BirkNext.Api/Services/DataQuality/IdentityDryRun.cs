using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.DataQuality;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.DataQuality;

/// <summary>
/// Requirement identity dry run (2026-11-27; approved as a read-only simulation, NOT as a migration). For every requirement without an acceptance
/// scenario in the baseline it shows the stored v1 source identity, the proposed source-aware v2 identity, and every stored record that refers to
/// the v1 identity — and how a v2 resolution would treat it. Nothing is written; an ambiguous relationship is reported, never resolved.
/// </summary>
public static class IdentityDryRun
{
    public const string V2RuleSet = "srcid/2";

    /// <summary>The stored v1 identity of a requirement without a scenario: the production formula (capability «specification» + «requirement:{module}|{id}»),
    /// where the module is the evidence's artifact scope (null at import, a module when the browser had one selected).</summary>
    public static string V1(string? module, string requirementId) => D.SourceIdentityOf(D.Specification, $"requirement:{module}|{requirementId}");

    /// <summary>The proposed v2 identity: rule set + normalized specification path + normalized requirement id. The application is reported, not hashed
    /// (resolution always happens inside one application's discovery). A moved or renamed file changes v2: moves are DETECTED, never silently relinked.</summary>
    public static string V2(string specificationPath, string requirementId) =>
        "tsr2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{V2RuleSet}|{NormalizePath(specificationPath)}|{ReferenceSpecParser.NormalizeRequirementId(requirementId)}")))[..16].ToLowerInvariant();

    public static string NormalizePath(string path) => path.Replace('\\', '/').Trim().Trim('/').Replace("/./", "/").ToLowerInvariant();

    public sealed record Requirement(string Path, string Id, string V1, string? Statement);
    public sealed record Reference(string Kind, string Text, string V1, string? Path);

    public sealed record Result(List<IdentityDryRunRow> Rows, List<Requirement> Requirements, List<Reference> References, Dictionary<string, List<string>> RepeatedDisplayIds,
        Dictionary<string, List<string>> SharedV1, List<string> V1Variants, List<string> Relocations);

    public static Result Run(DataQualitySnapshot s, ReferenceCalculator.Expected e)
    {
        var application = s.Application;
        var version = e.Baseline is { } b ? $"katalog r{b.Revision} · {b.CapturedAt:yyyy-MM-dd HH:mm}{(b.ArchiveSha256 is { Length: >= 12 } sha ? $" · arkiv {sha[..12]}" : "")}" : null;
        var requirements = Requirements(e.Baseline?.EvidenceJson);
        var byV1 = requirements.GroupBy(r => r.V1, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var references = References(s, e);
        var v1Known = byV1.Keys.ToHashSet(StringComparer.Ordinal);

        var rows = new List<IdentityDryRunRow>();
        foreach (var r in requirements)
        {
            var sameV1 = byV1[r.V1];
            var refs = references.Where(x => x.V1 == r.V1).ToList();
            IdentityResolution resolution; string note;
            if (sameV1.Count == 1)
            {
                resolution = IdentityResolution.NotAffected;
                note = "Bare ett dokument har denne v1-identiteten: v2 endrer ikke hvilket krav relasjonene peker på.";
            }
            else
            {
                // Each referring record decides on its own specification path; without one it stays ambiguous (never guessed from the display id).
                var mine = refs.Where(x => x.Path is { } p && NormalizePath(p) == NormalizePath(r.Path)).ToList();
                var unplaced = refs.Where(x => x.Path is null || !sameV1.Any(o => NormalizePath(o.Path) == NormalizePath(x.Path))).ToList();
                resolution = unplaced.Count > 0 ? IdentityResolution.Ambiguous : IdentityResolution.Unique;
                note = unplaced.Count > 0
                    ? $"{sameV1.Count} dokumenter deler v1-identiteten. {unplaced.Count} lagret(e) relasjon(er) har ingen spesifikasjonssti som avgjør hvilket av dem de gjelder — må vurderes av en person."
                    : $"{sameV1.Count} dokumenter deler v1-identiteten. {(mine.Count == 0 ? "Ingen lagrede relasjoner peker på kravet i dette dokumentet." : $"{mine.Count} relasjon(er) avgjøres entydig av sin egen spesifikasjonssti.")}";
                refs = [.. mine, .. unplaced];
            }
            rows.Add(Row(r, application, version, sameV1.Count, refs, resolution, note));
        }
        // Stored relationships to a REQUIREMENT identity no current document has: unresolved (the requirement left the source, or v1 came from another
        // selection scope). Only identities known to be requirement identities count — a case of an acceptance scenario is not a requirement relationship.
        var requirementIdentities = RequirementIdentitiesSeen(s);
        foreach (var g in references.Where(x => !v1Known.Contains(x.V1) && requirementIdentities.Contains(x.V1)).GroupBy(x => x.V1, StringComparer.Ordinal))
            rows.Add(Row(new Requirement(g.First().Path ?? "", "", g.Key, null), application, version, 0, [.. g], IdentityResolution.Unresolved,
                "Lagrede relasjoner peker på en v1-identitet som ingen spesifikasjon i grunnlaget har (kravet er fjernet, eller identiteten ble laget i et annet modulvalg). Må vurderes."));

        var repeated = requirements.GroupBy(r => ReferenceSpecParser.NormalizeRequirementId(r.Id), StringComparer.Ordinal)
            .Where(g => g.Select(x => NormalizePath(x.Path)).Distinct().Count() > 1).ToDictionary(g => g.Key, g => g.Select(x => x.Path).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var shared = byV1.Where(kv => kv.Value.Select(x => NormalizePath(x.Path)).Distinct().Count() > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(x => x.Path).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return new Result(rows, requirements, references, repeated, shared, V1Variants(s, requirements), Relocations(s, e, requirements));
    }

    private static IdentityDryRunRow Row(Requirement r, string? application, string? version, int documents, List<Reference> refs, IdentityResolution resolution, string note) => new()
    {
        V1 = r.V1, V2 = r.Path.Length > 0 && r.Id.Length > 0 ? V2(r.Path, r.Id) : "", RequirementId = r.Id, SpecificationPath = r.Path, Application = application,
        SourceVersion = version, DocumentsWithSameV1 = documents, Resolution = resolution, Note = note,
        TestCases = [.. refs.Where(x => x.Kind == "case").Select(x => x.Text)], PlanItems = [.. refs.Where(x => x.Kind == "plan-item").Select(x => x.Text)],
        ReadinessDecisions = [.. refs.Where(x => x.Kind == "readiness").Select(x => x.Text)], Approvals = [.. refs.Where(x => x.Kind == "approval").Select(x => x.Text)],
        Executions = [.. refs.Where(x => x.Kind == "execution").Select(x => x.Text)], Coverage = [.. refs.Where(x => x.Kind == "coverage").Select(x => x.Text)],
    };

    /// <summary>Requirements without a scenario in the baseline, with their stored v1 identity (computed from the stored artifact scope).</summary>
    public static List<Requirement> Requirements(string? evidenceJson)
    {
        var list = new List<Requirement>();
        if (evidenceJson is null) return list;
        using var doc = JsonDocument.Parse(evidenceJson);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var path = (DataQualitySnapshotReader.Str(item, "path") ?? "").Replace('\\', '/');
            if (!item.TryGetProperty("evidence", out var ev)) continue;
            var module = DataQualitySnapshotReader.Str(ev, "artifactScope") is { } m && m.Trim().Length > 0 ? m.Trim() : null;
            var named = ReferenceCalculator.Array(ev, "acceptanceScenarios").SelectMany(sc => ReferenceCalculator.Array(sc, "requirementIds"))
                .Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ReferenceCalculator.Array(ev, "requirements"))
                if (DataQualitySnapshotReader.Str(r, "id") is { } id && id.Trim().Length > 0 && !named.Contains(id) && list.All(x => !(x.Path == path && x.Id == id)))
                    list.Add(new Requirement(path, id, V1(module, id), DataQualitySnapshotReader.Str(r, "text")));
        }
        return list;
    }

    /// <summary>Every stored record that refers to a source identity: cases (and their approvals and coverage), plan items, readiness decisions and executions.</summary>
    public static List<Reference> References(DataQualitySnapshot s, ReferenceCalculator.Expected e)
    {
        var refs = new List<Reference>();
        foreach (var c in e.Cases.Where(c => c.Identity is not null))
        {
            var label = $"{c.PlanName} · {c.CaseId}";
            refs.Add(new("case", label, c.Identity!, c.SpecificationPath));
            refs.Add(new("coverage", $"{c.SpecificationPath ?? "(ingen sti)"} | {c.Identity}", c.Identity!, c.SpecificationPath));
            if (c.Review == "Accepted") refs.Add(new("approval", $"{label} (godkjent)", c.Identity!, c.SpecificationPath));
        }
        var outcomes = s.Runs.Where(r => r.CaseId.StartsWith("occ:scenario:", StringComparison.Ordinal)).GroupBy(r => (r.DraftId, Id: r.CaseId["occ:scenario:".Length..])).ToDictionary(g => g.Key, g => g.Count());
        foreach (var row in s.Drafts.Where(d => d.Lifecycle == "Active"))
        {
            try
            {
                using var doc = JsonDocument.Parse(row.ContentJson);
                var name = DataQualitySnapshotReader.Str(doc.RootElement, "planName") ?? "";
                foreach (var p in ReferenceCalculator.Array(doc.RootElement, "plannedScenarios"))
                    if (DataQualitySnapshotReader.Str(p, "sourceIdentity") is { Length: > 0 } si)
                    {
                        var scenarioId = DataQualitySnapshotReader.Str(p, "scenarioId") ?? "";
                        refs.Add(new("plan-item", $"{name} · {scenarioId}", si, null));
                        if (outcomes.TryGetValue((row.Id, scenarioId), out var n)) refs.Add(new("execution", $"{name} · {scenarioId} ({n} registrering(er))", si, null));
                    }
                foreach (var c in ReferenceCalculator.Array(doc.RootElement, "cases"))
                    if (c.TryGetProperty("generated", out var g) && DataQualitySnapshotReader.Str(g, "sourceIdentity") is { Length: > 0 } si
                        && c.TryGetProperty("source", out var src) && DataQualitySnapshotReader.Str(src, "scenarioId") is { } sid && outcomes.TryGetValue((row.Id, sid), out var n))
                        refs.Add(new("execution", $"{name} · {sid} ({n} registrering(er))", si, null));
            }
            catch (JsonException) { }
        }
        foreach (var g in s.Groupings)
        {
            try
            {
                using var doc = JsonDocument.Parse(g.ContentJson);
                foreach (var d in ReferenceCalculator.Array(doc.RootElement, "preparation"))
                    if (DataQualitySnapshotReader.Str(d, "scenarioId") is { Length: > 0 } sid)
                        refs.Add(new("readiness", $"{g.SpecificationPath} · {DataQualitySnapshotReader.Str(d, "criterion")} ({DataQualitySnapshotReader.Str(d, "by")})", sid, g.SpecificationPath));
            }
            catch (JsonException) { }
        }
        return refs;
    }

    /// <summary>Every source identity a stored discovery gave to a requirement without a scenario («requirement:» canonical key).</summary>
    private static HashSet<string> RequirementIdentitiesSeen(DataQualitySnapshot s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in s.Discoveries)
            try
            {
                using var doc = JsonDocument.Parse(d.ContentJson);
                foreach (var c in ReferenceCalculator.Array(doc.RootElement, "candidates"))
                    if ((DataQualitySnapshotReader.Str(c, "canonicalKey") ?? "").StartsWith("requirement:", StringComparison.Ordinal)
                        && DataQualitySnapshotReader.Str(c, "sourceIdentity") is { Length: > 0 } si)
                        set.Add(si);
            }
            catch (JsonException) { }
        return set;
    }

    /// <summary>The same (document, requirement) seen under more than one v1 identity: v1 depends on the module that was selected when it was read.</summary>
    private static List<string> V1Variants(DataQualitySnapshot s, List<Requirement> baseline)
    {
        var seen = baseline.GroupBy(r => (NormalizePath(r.Path), r.Id)).ToDictionary(g => g.Key, g => g.Select(x => x.V1).ToHashSet(StringComparer.Ordinal));
        foreach (var d in s.Discoveries)
        {
            try
            {
                using var doc = JsonDocument.Parse(d.ContentJson);
                foreach (var c in ReferenceCalculator.Array(doc.RootElement, "candidates"))
                {
                    var key = DataQualitySnapshotReader.Str(c, "canonicalKey") ?? "";
                    if (!key.StartsWith("requirement:", StringComparison.Ordinal)) continue;
                    var id = key[(key.LastIndexOf('|') + 1)..];
                    var file = ReferenceCalculator.Array(c, "evidence").Select(e => DataQualitySnapshotReader.Str(e, "reference")).FirstOrDefault(r => r?.Contains('#') == true)?.Split('#')[0];
                    if (file is null || DataQualitySnapshotReader.Str(c, "sourceIdentity") is not { Length: > 0 } si) continue;
                    var k = (NormalizePath(file), id);
                    if (!seen.TryGetValue(k, out var set)) continue;
                    set.Add(si);
                }
            }
            catch (JsonException) { }
        }
        return [.. seen.Where(kv => kv.Value.Count > 1).Select(kv => $"{kv.Key.Item1} · {kv.Key.Id}: {string.Join(", ", kv.Value.Order(StringComparer.Ordinal))}")];
    }

    /// <summary>Requirements that left one path and appear under another with the same id and the same statement, between the previous catalog revision
    /// and the baseline: «flyttet? — må vurderes». Never relinked automatically.</summary>
    private static List<string> Relocations(DataQualitySnapshot s, ReferenceCalculator.Expected e, List<Requirement> now)
    {
        if (e.Baseline is null) return [];
        var previous = s.Catalogs.Where(c => c.Revision < e.Baseline.Revision && c.EvidenceJson is not null).OrderByDescending(c => c.Revision).FirstOrDefault();
        if (previous is null) return [];
        var before = Requirements(previous.EvidenceJson);
        static string Fp(string? t) => System.Text.RegularExpressions.Regex.Replace((t ?? "").ToLowerInvariant(), @"\s+", " ").Trim();
        var gone = before.Where(b => !now.Any(n => NormalizePath(n.Path) == NormalizePath(b.Path) && n.Id == b.Id)).ToList();
        return [.. gone.SelectMany(g => now.Where(n => n.Id == g.Id && Fp(n.Statement) == Fp(g.Statement) && Fp(n.Statement).Length > 0
            && !before.Any(b => NormalizePath(b.Path) == NormalizePath(n.Path) && b.Id == n.Id))
            .Select(n => $"{g.Id}: {g.Path} → {n.Path} (samme kravtekst; må vurderes, kobles ikke automatisk)"))];
    }
}
