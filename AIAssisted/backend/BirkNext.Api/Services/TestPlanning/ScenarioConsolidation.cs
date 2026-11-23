using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Cross-provider consolidation and conflict validation of discovered candidates (2026-11-23). Deterministic and conservative:
/// <list type="bullet">
/// <item><b>A — exact duplicate</b>: two sources describe the same situation (starting point and action) with the same expected result, or a guide
/// scenario names the candidate it was generated from. Merged into one candidate; all evidence, requirement ids and the merged identities are kept.</item>
/// <item><b>B — equivalent</b>: the same situation, compatible expected results in other words. Kept separate, noted both ways.</item>
/// <item><b>C — related</b>: they share a requirement id but describe different situations. Kept separate.</item>
/// <item><b>D — conflicting</b>: the same situation, contradictory expected results (disjoint HTTP status codes, or accepted vs refused). Every claim is
/// kept with its provenance; nothing is chosen. The case cannot be Ready until a person resolves it.</item>
/// <item><b>E — variant</b>: a similar situation where one is the negation of the other (a positive and a negative case). Never merged.</item>
/// </list>
/// Different statuses for different conditions are not a conflict: without the same situation two candidates are at most related. Candidates of the
/// same source are never compared (they are that source's own different scenarios).
/// </summary>
public static partial class ScenarioConsolidation
{
    public const double SameSituation = 0.8, SimilarSituation = 0.5, CompatibleExpected = 0.6;

    /// <summary>The evidence kind a guide scenario uses to name the discovered candidate it was generated from (its <see cref="DiscoveredScenario.SourceIdentity"/>).</summary>
    public const string DerivedFrom = "derived-from";

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "when", "then", "given", "are", "was", "has", "have", "will", "from", "into", "its", "their", "there", "which",
        "som", "og", "for", "med", "det", "den", "når", "så", "gitt", "skal", "har", "til", "fra", "er", "eller", "ved", "på", "av", "en", "et", "ei",
        "carry", "out", "trigger", "prepare", "starting", "situation", "test", "environment",
    };

    private static readonly string[] Negations = ["not", "no", "never", "without", "missing", "invalid", "unknown", "ikke", "ingen", "aldri", "uten", "mangler", "ugyldig", "ukjent"];
    private static readonly string[] Positive = ["accepted", "accepts", "accept", "stored", "saved", "created", "held", "succeeds", "success", "ok", "godkjent", "mottatt", "lagret", "opprettet", "holdt", "vellykket"];
    private static readonly string[] Refusal = ["rejected", "rejects", "refused", "refuses", "denied", "fails", "error", "unavailable", "avvist", "avviser", "nektet", "feiler", "feil", "utilgjengelig"];

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[1-5]\d\d(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex StatusCode();

    /// <summary>The comparable words of a text: content words, every number and every negation (so "returns 0 rows" ≠ "returns 2 rows" and
    /// "with no role" ≠ "with role").</summary>
    public static HashSet<string> Words(string? text) =>
        [.. Token().Matches((text ?? "").ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.All(char.IsDigit) || Negations.Contains(w) || w.Length >= 3 && !Stop.Contains(w))];

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "a", "an", "the", "en", "et", "ei" };

    /// <summary>The text in order, lower case, articles left out: two texts are the same statement only when this is equal.</summary>
    public static string Normalized(string? text) =>
        string.Join(' ', Token().Matches((text ?? "").ToLowerInvariant()).Select(m => m.Value).Where(w => !Articles.Contains(w)));

    public static double Similarity(HashSet<string> a, HashSet<string> b) => a.Count == 0 || b.Count == 0 ? 0 : (double)a.Count(b.Contains) / a.Union(b).Count();

    /// <summary>The situation a candidate describes: its starting situation and its action (never its expected result).</summary>
    public static string Situation(DiscoveredScenario c) =>
        string.Join(" ", c.Preconditions.Concat(c.Steps.Take(1).Select(s => s.Action))
            .Concat(c.Template.Where(t => t.Key is "given" or "when" or "operation" or "trigger").Select(t => t.Value ?? "")));

    public static IReadOnlySet<string> StatusCodes(string? text) => StatusCode().Matches(text ?? "").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>+1 accepted/succeeds, -1 refused/fails, 0 neither or both.</summary>
    public static int Polarity(string? expected)
    {
        var words = Token().Matches((expected ?? "").ToLowerInvariant()).Select(m => m.Value).ToList();
        var positive = words.Count(Positive.Contains);
        var refusal = words.Count(Refusal.Contains);
        var negated = words.Any(w => w is "not" or "ikke" or "never" or "aldri");
        if (negated) (positive, refusal) = (refusal, positive);
        return positive > 0 && refusal == 0 ? 1 : refusal > 0 && positive == 0 ? -1 : 0;
    }

    private static bool Negated(HashSet<string> words) => words.Any(w => Negations.Contains(w));

    /// <summary>Whether two expected results contradict each other for the same situation.</summary>
    public static bool Contradict(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var codesA = StatusCodes(a);
        var codesB = StatusCodes(b);
        if (codesA.Count > 0 && codesB.Count > 0) return !codesA.Overlaps(codesB);
        var pa = Polarity(a);
        var pb = Polarity(b);
        return pa != 0 && pb != 0 && pa != pb;
    }

    private static int Rank(string capability) => capability switch
    {
        TestScenarioDiscoveryEngine.Specification => 0,
        TestScenarioDiscoveryEngine.SystemGuide => 1,
        TestScenarioDiscoveryEngine.ApiContract => 2,
        TestScenarioDiscoveryEngine.IntegrationJourneys => 3,
        TestScenarioDiscoveryEngine.UiSource => 4,
        _ => 5,
    };

    public static List<DiscoveredScenario> Apply(List<DiscoveredScenario> candidates)
    {
        var list = candidates.ToList();
        // 1. A guide scenario generated from a discovered candidate is that candidate: merged into it (the guide adds its review and evidence).
        var bySource = list.Where(c => c.SourceIdentity.Length > 0).GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var absorbed = new HashSet<string>(StringComparer.Ordinal);
        var versionConflicts = new List<(string A, string B, ScenarioConflict Conflict)>();
        foreach (var g in list.Where(c => c.CapabilityId == TestScenarioDiscoveryEngine.SystemGuide).ToList())
        {
            var derived = g.Evidence.FirstOrDefault(e => e.Kind == DerivedFrom)?.Reference;
            if (derived is null || !bySource.TryGetValue(derived, out var target) || target.CandidateId == g.CandidateId) continue;
            // Merged only while the guide still says what the source says; a guide written from an older source version is kept apart with a conflict.
            if (g.ExpectedBehaviour is null || Similarity(Words(g.ExpectedBehaviour), Words(target.ExpectedBehaviour)) >= 0.999)
                Merge(list, target, g, absorbed, "System Understanding-veiledningen beskriver det samme scenarioet (generert fra denne kilden).");
            else
                versionConflicts.Add((g.CandidateId, target.CandidateId, new ScenarioConflict
                {
                    Kind = ConflictKind.DifferentSourceVersion, Claims = [Claim(g), Claim(target)],
                    Explanation = "System Understanding-veiledningen og gjeldende kildeversjon beskriver ulike forventede resultater. Veiledningen kan være skrevet fra en eldre kildeversjon. Dette må avklares før testtilfellet kan godkjennes.",
                }));
        }
        list.RemoveAll(c => absorbed.Contains(c.CandidateId));

        // 2. Pairwise comparison across sources, only for candidates that share situation words (inverted index; frequent words are skipped).
        var situations = list.ToDictionary(c => c.CandidateId, c => Words(Situation(c)), StringComparer.Ordinal);
        var expected = list.ToDictionary(c => c.CandidateId, c => Words(c.ExpectedBehaviour), StringComparer.Ordinal);
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
            foreach (var w in situations[list[i].CandidateId]) (index.TryGetValue(w, out var l) ? l : index[w] = []).Add(i);
        var pairs = new HashSet<(int, int)>();
        foreach (var l in index.Values.Where(l => l.Count is > 1 and <= 60))
            for (var x = 0; x < l.Count; x++)
                for (var y = x + 1; y < l.Count; y++)
                    if (list[l[x]].CapabilityId != list[l[y]].CapabilityId) pairs.Add((l[x], l[y]));

        // Candidates of different sources that name the same requirement are compared too (at most related when their situations differ).
        foreach (var group in list.Select((c, i) => (c, i)).SelectMany(x => x.c.RequirementIds.Select(r => (Key: r.ToUpperInvariant(), x.i))).GroupBy(x => x.Key).Where(g => g.Count() is > 1 and <= 60))
        {
            var members = group.Select(x => x.i).Distinct().ToList();
            for (var x = 0; x < members.Count; x++)
                for (var y = x + 1; y < members.Count; y++)
                    if (list[members[x]].CapabilityId != list[members[y]].CapabilityId) pairs.Add((Math.Min(members[x], members[y]), Math.Max(members[x], members[y])));
        }
        var notes = list.ToDictionary(c => c.CandidateId, _ => new List<ScenarioRelationNote>(), StringComparer.Ordinal);
        var conflicts = list.ToDictionary(c => c.CandidateId, _ => new List<ScenarioConflict>(), StringComparer.Ordinal);
        foreach (var (a, b, conflict) in versionConflicts.Where(v => conflicts.ContainsKey(v.A) && conflicts.ContainsKey(v.B)))
        {
            conflicts[a].Add(conflict); conflicts[b].Add(conflict);
            notes[a].Add(new(ScenarioRelation.Conflicting, b, "Veiledningen og kilden er uenige om forventet resultat."));
            notes[b].Add(new(ScenarioRelation.Conflicting, a, "Veiledningen og kilden er uenige om forventet resultat."));
        }
        var merges = new List<(int Keep, int Drop)>();
        foreach (var (i, j) in pairs.OrderBy(p => p.Item1).ThenBy(p => p.Item2))
        {
            var a = list[i]; var b = list[j];
            if (Derived(a) == b.SourceIdentity || Derived(b) == a.SourceIdentity) continue; // already decided above
            var sa = situations[a.CandidateId]; var sb = situations[b.CandidateId];
            var sharedRequirement = a.RequirementIds.Intersect(b.RequirementIds, StringComparer.OrdinalIgnoreCase).Any();
            var situation = sa.Count < 2 || sb.Count < 2 ? 0 : Similarity(sa, sb);
            if (situation < SimilarSituation)
            {
                if (sharedRequirement) Note(notes, a, b, ScenarioRelation.Related, "Samme krav, men ulik situasjon (ulike betingelser er ikke en konflikt).");
                continue;
            }
            if (situation >= SameSituation && Negated(sa) != Negated(sb))
            {
                Note(notes, a, b, ScenarioRelation.Variant, "Samme handling, men det ene scenarioet er den negative varianten av det andre.");
                continue;
            }
            if (situation >= SameSituation)
            {
                if (Contradict(a.ExpectedBehaviour, b.ExpectedBehaviour))
                {
                    var conflict = new ScenarioConflict
                    {
                        Kind = ConflictKind.ContradictoryExpectedResult,
                        Claims = [Claim(a), Claim(b)],
                        Explanation = "Ulike kilder beskriver forskjellige forventede resultater for samme situasjon. Dette må avklares før testtilfellet kan godkjennes.",
                    };
                    conflicts[a.CandidateId].Add(conflict);
                    conflicts[b.CandidateId].Add(conflict);
                    Note(notes, a, b, ScenarioRelation.Conflicting, "Samme situasjon, motstridende forventet resultat.");
                    continue;
                }
                var ea = expected[a.CandidateId]; var eb = expected[b.CandidateId];
                // Exact duplicate: the same statement word for word (in order, numbers and negations included) — never a similar one.
                if (ea.Count > 0 && Normalized(Situation(a)) == Normalized(Situation(b)) && Normalized(a.ExpectedBehaviour) == Normalized(b.ExpectedBehaviour))
                {
                    merges.Add(Rank(a.CapabilityId) <= Rank(b.CapabilityId) ? (i, j) : (j, i));
                    continue;
                }
                if (ea.Count == 0 || eb.Count == 0 || Similarity(ea, eb) >= CompatibleExpected)
                {
                    Note(notes, a, b, ScenarioRelation.Equivalent, "Samme situasjon og forenlig forventet resultat fra en annen kilde (ikke slått sammen; vurder om begge trengs).");
                    continue;
                }
            }
            if (sharedRequirement) Note(notes, a, b, ScenarioRelation.Related, "Samme krav, men ulik situasjon eller ulikt forventet resultat (ulike betingelser er ikke en konflikt).");
        }

        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var keptAs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (keep, drop) in merges)
        {
            if (dropped.Contains(list[keep].CandidateId) || dropped.Contains(list[drop].CandidateId)) continue;
            keptAs[list[drop].CandidateId] = list[keep].CandidateId;
            Merge(list, list[keep], list[drop], dropped, "Eksakt duplikat fra en annen kilde: samme situasjon og samme forventede resultat.");
        }
        string Kept(string id) => keptAs.TryGetValue(id, out var to) ? to : id;
        return [.. list.Where(c => !dropped.Contains(c.CandidateId)).Select(c => c with
        {
            Relations = [.. c.Relations.Concat(notes.GetValueOrDefault(c.CandidateId) ?? []).Concat(keptAs.Where(k => k.Value == c.CandidateId).SelectMany(k => notes.GetValueOrDefault(k.Key) ?? []))
                .Select(r => r with { OtherCandidateId = Kept(r.OtherCandidateId) }).Where(r => r.OtherCandidateId != c.CandidateId).DistinctBy(r => (r.Relation, r.OtherCandidateId))],
            Conflicts = [.. c.Conflicts.Concat(conflicts.GetValueOrDefault(c.CandidateId) ?? [])
                .Concat(keptAs.Where(k => k.Value == c.CandidateId).SelectMany(k => conflicts.GetValueOrDefault(k.Key) ?? [])).Distinct()],
            Gaps = (conflicts.GetValueOrDefault(c.CandidateId)?.Count ?? 0) > 0 && c.Gaps.All(g => g.Kind != EvidenceGapKind.ContradictoryEvidence)
                ? [.. c.Gaps, new EvidenceGap(EvidenceGapKind.ContradictoryEvidence, "Ulike kilder beskriver forskjellige forventede resultater for samme situasjon.", "Kryssjekk av kilder",
                    "Avklar forventet resultat med fagansvarlig, og registrer avklaringen på testtilfellet.")]
                : c.Gaps,
        })];
    }

    private static string? Derived(DiscoveredScenario c) => c.Evidence.FirstOrDefault(e => e.Kind == DerivedFrom)?.Reference;

    private static ScenarioClaim Claim(DiscoveredScenario c) =>
        new(c.CandidateId, c.SourceCategory, c.ExpectedBehaviour ?? "", c.Evidence.FirstOrDefault(e => e.Kind != DerivedFrom)?.Reference ?? c.CanonicalKey);

    private static void Note(Dictionary<string, List<ScenarioRelationNote>> notes, DiscoveredScenario a, DiscoveredScenario b, ScenarioRelation relation, string reason)
    {
        notes[a.CandidateId].Add(new(relation, b.CandidateId, reason));
        notes[b.CandidateId].Add(new(relation, a.CandidateId, reason));
    }

    /// <summary>Folds <paramref name="drop"/> into <paramref name="keep"/> in place: evidence, requirement ids, scope keys and identities are kept.</summary>
    private static void Merge(List<DiscoveredScenario> list, DiscoveredScenario keep, DiscoveredScenario drop, HashSet<string> removed, string reason)
    {
        var index = list.FindIndex(c => c.CandidateId == keep.CandidateId);
        var current = list[index];
        list[index] = current with
        {
            Evidence = [.. current.Evidence.Concat(drop.Evidence.Where(e => e.Kind != DerivedFrom)).DistinctBy(e => (e.Kind, e.Reference))],
            RequirementIds = [.. current.RequirementIds.Concat(drop.RequirementIds).Distinct(StringComparer.OrdinalIgnoreCase)],
            ScopeKeys = [.. current.ScopeKeys.Concat(drop.ScopeKeys).Distinct(StringComparer.Ordinal)],
            MergedFrom = [.. current.MergedFrom.Append(drop.SourceIdentity).Concat(drop.MergedFrom).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal)],
            // Nothing the dropped source said is lost: its open questions, clarifications, test data needs, gaps, conflicts and relations stay.
            OpenQuestions = [.. current.OpenQuestions.Concat(drop.OpenQuestions).Distinct(StringComparer.Ordinal)],
            Clarifications = [.. current.Clarifications.Concat(drop.Clarifications).DistinctBy(k => k.Id)],
            TestDataNeeds = [.. current.TestDataNeeds.Concat(drop.TestDataNeeds).Distinct(StringComparer.Ordinal)],
            Gaps = [.. current.Gaps.Concat(drop.Gaps).DistinctBy(g => (g.Kind, g.Question))],
            Conflicts = [.. current.Conflicts.Concat(drop.Conflicts)],
            Relations = [.. current.Relations.Concat(drop.Relations)],
            ExistingTests = [.. current.ExistingTests.Concat(drop.ExistingTests).DistinctBy(t => t.TestId)],
            SourcesChecked = [.. current.SourcesChecked, new EvidenceSourceCheck(drop.SourceCategory, true, reason)],
            // A reviewed guide confirms the expectation; otherwise the stronger source keeps its own confidence.
            Confidence = drop.CapabilityId == TestScenarioDiscoveryEngine.SystemGuide && drop.Confidence == CandidateConfidence.ExplicitSpecification ? CandidateConfidence.ExplicitSpecification : current.Confidence,
        };
        removed.Add(drop.CandidateId);
    }
}
