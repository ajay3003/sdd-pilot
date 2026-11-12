namespace BirkNext.Interpretation;

/// <summary>
/// A specification requirement a System Understanding guide names for a scenario (GuideEvidence.RequirementId on the scenario's own evidence or on a
/// rule that lists the scenario). <see cref="SpecFile"/> is the specification the guide cites with it ("Skole/specs/001-skoletjenesten/spec.md"):
/// it scopes the id to one feature pack. Without it the id is resolved only when exactly one feature pack defines it.
/// </summary>
public sealed record GuideRequirementReference(string RequirementId, string? SpecFile);

/// <summary>
/// One System Understanding "What to test" scenario as the canonical engine reads it (MT-7). <see cref="ContentFingerprint"/> is the scenario's own
/// content fingerprint (the same one Test Plan Suggestions stores on a draft), so a review made on another scenario text is detectable.
/// <see cref="Published"/> is true only for a published (reviewed) guide revision: a requirement named by an unreviewed draft is a suggestion.
/// <see cref="TestReferences"/> are the test definitions the guide names for the scenario (its ExistingTests and the test ids of its rules).
/// </summary>
public sealed record GuideScenarioInput(
    string GuideKey, int Revision, bool Published, string ScenarioId, string Title, string ContentFingerprint, IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> TestReferences, IReadOnlyList<GuideRequirementReference> RequirementReferences);

public static partial class CorrelationRules
{
    /// <summary>A guide names the requirement for the scenario (explicit when the guide revision is published; a candidate while it is a draft).</summary>
    public const string GuideScenarioExplicitRule = "CR-S-EXPLICIT";

    /// <summary>A test the guide names for the scenario explicitly verifies the requirement: a candidate only, a person confirms it.</summary>
    public const string GuideScenarioViaTestRule = "CR-S-VIA-TEST";

    /// <summary>Two requirements of different feature packs share an id and nothing the guide states says which one.</summary>
    public const string GuideScenarioAmbiguousRule = "CR-S-SCOPE";

    public static readonly IReadOnlySet<string> GuideScenarioRules = new HashSet<string>(StringComparer.Ordinal) { GuideScenarioExplicitRule, GuideScenarioViaTestRule, GuideScenarioAmbiguousRule };

    /// <summary>The canonical identity of a guide scenario: never a spec acceptance scenario (those are module/feature/key).</summary>
    public static string GuideScenarioIdentity(string guideKey, string scenarioId) => $"system-understanding/{CanonicalKeys.Normalize(guideKey)}/{scenarioId}";

    /// <summary>Module and feature-pack key of a specification path ("Skole/specs/001-skoletjenesten/spec.md" → Skole, 001-skoletjenesten).</summary>
    public static (string Module, string Feature)? SpecScope(string? specFile)
    {
        if (string.IsNullOrWhiteSpace(specFile)) return null;
        var parts = specFile.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(parts, p => string.Equals(p, "specs", StringComparison.OrdinalIgnoreCase));
        return at >= 1 && at + 1 < parts.Length ? (parts[at - 1], parts[at + 1]) : null;
    }

    /// <summary>Fingerprint of a requirement's text as the specification states it: a changed requirement makes an earlier review need revalidation.</summary>
    public static string RequirementTextFingerprint(CanonicalEntityFact requirement) =>
        CanonicalKeys.Fingerprint([requirement.Attribute("text") ?? requirement.DisplayName]);

    /// <summary>
    /// Scenario → requirement associations (VERIFIES, source = the guide scenario). Never from title or text similarity:
    /// (1) a requirement id the guide states for the scenario, resolved to one feature pack; (2) a test the guide names for the scenario that the
    /// source states explicitly verifies a requirement — always a candidate. Every relation carries the scenario content fingerprint and the
    /// requirement text fingerprint as evidence, so a decision made on an older scenario or requirement text needs revalidation.
    /// </summary>
    static void GuideScenarios(IReadOnlyList<GuideScenarioInput> scenarios, IReadOnlyDictionary<string, CanonicalEntityFact> known,
        IReadOnlyDictionary<string, CanonicalRelationFact> established, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation, List<string> limitations)
    {
        if (scenarios.Count == 0) return;
        var requirements = known.Values.Where(e => e.Type == CanonicalEntityType.Requirement && e.Attribute("id") is not null && e.Attribute("module") is not null).ToList();
        var byId = requirements.GroupBy(e => e.Attribute("id")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        var tests = known.Values.Where(e => e.Type == CanonicalEntityType.Test).ToList();
        // Test → requirement links the source states: explicitly (a trait or structured reference) or informally (the id written in the test file or
        // name, CR-M-CANDIDATE). Ambiguous ids (several feature packs) are never followed.
        var verifies = established.Values.Where(r => r.Type == CanonicalRelationType.Verifies
                && (r.Resolution is RelationResolution.Explicit or RelationResolution.Derived || r.Resolution == RelationResolution.Candidate && r.RuleId == TestCandidateRule))
            .ToLookup(r => r.SourceKey, StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var s in scenarios.OrderBy(s => s.GuideKey, StringComparer.Ordinal).ThenBy(s => s.ScenarioId, StringComparer.Ordinal))
        {
            var fact = new CanonicalEntityFact(CanonicalEntityType.Scenario, GuideScenarioIdentity(s.GuideKey, s.ScenarioId), s.Title, $"{s.GuideKey}/{s.ScenarioId}",
                "System Understanding guide + scenario id", [new("guide-scenario", $"{s.GuideKey}/{s.ScenarioId}", $"revision {s.Revision}")],
                Attributes: Attrs(("source", "system-understanding"), ("guide", s.GuideKey), ("scenarioId", s.ScenarioId), ("guideRevision", s.Revision.ToString()),
                    ("published", s.Published ? "true" : "false"), ("contentFingerprint", s.ContentFingerprint), ("rules", string.Join(",", s.RuleIds))));
            entity(fact);
            var scenarioEvidence = new CorrelationEvidence("guide-scenario", $"{s.GuideKey}/{s.ScenarioId}", $"content {s.ContentFingerprint}");

            CorrelationEvidence RequirementEvidence(CanonicalEntityFact r) => new("requirement-text", r.IdentityKey, $"text {RequirementTextFingerprint(r)}");

            foreach (var reference in s.RequirementReferences.DistinctBy(r => (NormalizeRequirement(r.RequirementId), r.SpecFile)))
            {
                var id = NormalizeRequirement(reference.RequirementId) ?? reference.RequirementId.Trim();
                var candidates = byId.GetValueOrDefault(id) ?? [];
                if (SpecScope(reference.SpecFile) is { } scope)
                    candidates = [.. candidates.Where(r => string.Equals(CanonicalKeys.Normalize(r.Attribute("module") ?? ""), CanonicalKeys.Normalize(scope.Module), StringComparison.Ordinal)
                        && string.Equals(CanonicalKeys.Normalize(r.Attribute("feature") ?? ""), CanonicalKeys.Normalize(scope.Feature), StringComparison.Ordinal))];
                var stated = new CorrelationEvidence("guide-requirement-reference", $"{s.GuideKey}/{s.ScenarioId} → {id}", reference.SpecFile);
                if (candidates.Count == 0) { unresolved.Add($"{s.GuideKey}/{s.ScenarioId} → {id}{(reference.SpecFile is null ? "" : " (" + reference.SpecFile + ")")}"); continue; }
                if (candidates.Count == 1)
                {
                    var r = candidates[0];
                    relation(new(fact.Key, r.Key, CanonicalRelationType.Verifies, s.Published ? RelationResolution.Explicit : RelationResolution.Candidate,
                        s.Published ? CorrelationConfidence.Explicit : CorrelationConfidence.High, GuideScenarioExplicitRule, [scenarioEvidence, stated, RequirementEvidence(r)],
                        s.Published
                            ? $"The published guide (revision {s.Revision}) names {id} for this scenario. It says what the scenario is meant to cover, not that a test ran."
                            : $"The guide draft (revision {s.Revision}, not reviewed) names {id} for this scenario. Confirm it in Relationship Review before it counts."));
                    continue;
                }
                foreach (var r in candidates)
                    relation(new(fact.Key, r.Key, CanonicalRelationType.Verifies, RelationResolution.Ambiguous, CorrelationConfidence.Low, GuideScenarioAmbiguousRule,
                        [scenarioEvidence, stated, RequirementEvidence(r)],
                        $"The guide names {id}, which {candidates.Count} feature packs define, and cites no specification that says which one. Choose the pack it covers."));
            }

            // A test the guide names for this scenario (class or class.method) that explicitly verifies a requirement. Suggestion only.
            var names = s.TestReferences.SelectMany(t => t.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(t => t.Length > 2 && !t.Contains(' ')).Distinct(StringComparer.Ordinal).ToList();
            foreach (var name in names)
                foreach (var test in tests.Where(t => TestMatches(t, name)))
                    foreach (var link in verifies[test.Key])
                    {
                        // Only a requirement a specification feature pack defines has a verified identity; an id known only because a test names it
                        // (no module/feature) is never proposed as covered.
                        if (!known.TryGetValue(link.TargetKey, out var r) || r.Type != CanonicalEntityType.Requirement || r.Attribute("module") is null) continue;
                        var stated = link.Resolution != RelationResolution.Candidate;
                        relation(new(fact.Key, r.Key, CanonicalRelationType.Verifies, RelationResolution.Candidate, stated ? CorrelationConfidence.Medium : CorrelationConfidence.Low, GuideScenarioViaTestRule,
                            [scenarioEvidence, new("guide-test-reference", $"{s.GuideKey}/{s.ScenarioId} → {name}"), new("test-verifies", $"{CanonicalKeys.Normalize(name)} → {r.IdentityKey}", link.RuleId), RequirementEvidence(r)],
                            stated
                                ? $"The guide names the test {name} for this scenario, and that test explicitly references {r.Attribute("id") ?? r.DisplayName}. The scenario may cover the requirement; a person decides."
                                : $"The guide names the test {name} for this scenario, and {r.Attribute("id") ?? r.DisplayName} is written in that test's source (an informal reference, itself unconfirmed). Weak evidence: a person decides."));
                    }
        }
        if (unresolved.Count > 0)
            limitations.Add($"{unresolved.Count} requirement reference(s) in System Understanding guides match no requirement in this interpretation (not counted): {string.Join(", ", unresolved.Take(10))}.");
    }

    /// <summary>A guide's test reference matches a canonical test by its suite (class) name or its name (exact, or class.method), never by similarity.</summary>
    static bool TestMatches(CanonicalEntityFact test, string reference)
    {
        var suite = test.Attribute("suite");
        var name = test.DisplayName;
        static string Last(string s) => s[(s.LastIndexOf('.') + 1)..];
        if (reference.Contains('.'))
        {
            var (cls, method) = (reference[..reference.LastIndexOf('.')], Last(reference));
            return string.Equals(Last(name), method, StringComparison.Ordinal) && (suite is not null && string.Equals(Last(suite), Last(cls), StringComparison.Ordinal)
                || name.EndsWith(reference, StringComparison.Ordinal));
        }
        return suite is not null && string.Equals(Last(suite), reference, StringComparison.Ordinal)
            || string.Equals(name, reference, StringComparison.Ordinal)
            || name.Contains("." + reference + ".", StringComparison.Ordinal) || name.StartsWith(reference + ".", StringComparison.Ordinal);
    }
}
