using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

// ── Scenario-centric manual test planning (2026-11-20) ──────────────────────────────────────────────────────────────────────
// Scenario = what should be tested and why (a discovered scenario); test case = how it is tested and what result is expected. One scenario
// holds one or more cases of the same Test Plan draft. Nothing here is a second plan, scenario or requirements engine: cases stay in
// TestPlanDraft.Cases, requirements come from the project's specification (the same requirement list the links are checked against), and
// every suggestion is a proposal a tester confirms or rejects.
// Audit: docs/audits/birknext-scenario-centric-manual-test-planning-2026-11-20.md.

/// <summary>Who wrote the translated text: a translation service, a person, or a person editing a machine translation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationOrigin { Machine, Manual, MachineEdited }

/// <summary>The text of a test case in one other language (default Norwegian bokmål). Kept beside the original (which stays authoritative and is never replaced); bound to the original
/// text it translates by <see cref="SourceFingerprint"/>, so a later change of the original is visible as "translated from an older text".</summary>
public sealed record TestCaseTranslation
{
    public string Language { get; init; } = ScenarioPlanning.Norwegian;
    public string? Title { get; init; }
    public string? Purpose { get; init; }
    public string? Preconditions { get; init; }
    public List<string> Steps { get; init; } = [];
    public List<string?> StepExpected { get; init; } = [];
    public string? ExpectedResult { get; init; }
    /// <summary>The scenario's starting situation, trigger and expected business outcome (Given/When/Then) as the source states them.</summary>
    public string? Given { get; init; }
    public string? When { get; init; }
    public string? Then { get; init; }
    public string? ObservationMethod { get; init; }
    public string? Environment { get; init; }
    public string? Roles { get; init; }
    public string? Dependencies { get; init; }
    /// <summary>SHA-256 of the original text (title, purpose, preconditions, steps, step results, expected result) this translation was made from.</summary>
    public string SourceFingerprint { get; init; } = "";
    public TranslationOrigin Origin { get; init; }
    /// <summary>The translation service when the text came from one (e.g. "Anthropic claude-…"); null when written by a person.</summary>
    public string? Provider { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>A user story's text in another language (title, description, why this priority, Independent Test), kept beside the original.</summary>
public sealed record StoryTranslation
{
    public string StoryKey { get; init; } = "";
    public string Language { get; init; } = ScenarioPlanning.Norwegian;
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? WhyThisPriority { get; init; }
    public string? IndependentTest { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public TranslationOrigin Origin { get; init; }
    public string? Provider { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>A requirement's wording in another language, for display beside links and suggestions only (the specification is never changed).
/// Bound to the wording it translates by <see cref="SourceSha256"/>.</summary>
public sealed record RequirementTextTranslation(string RequirementId, string SpecificationPath, string Language, string Text, string SourceSha256, string? Provider, DateTimeOffset At);

/// <summary>Translate every scenario of the plan (and their user stories and linked requirement wording) into <see cref="Target"/>.</summary>
public sealed record TranslatePlanRequest(int BaseRevision, bool Overwrite = false, string? Target = null);

/// <summary>Whether a case's translated text matches its current original.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationState { None, Current, SourceChanged }

/// <summary>A requirement suggestion the tester rejected for a case.</summary>
public sealed record RejectedRequirementSuggestion
{
    public string RequirementId { get; init; } = "";
    public string SpecificationPath { get; init; } = "";
    public string RejectedBy { get; init; } = "";
    public DateTimeOffset RejectedAt { get; init; }
}

/// <summary>How strongly the evidence supports a requirement suggestion. A = explicit id, B = the specification's structure, C = the same
/// behaviour stated in both texts (with the shared wording as the explanation), D = possible but not established. Never a confirmed link.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementSuggestionLevel { Explicit, Structural, Semantic, Weak }

/// <summary>One suggested requirement for a scenario or case, with why it was suggested and the file it comes from.</summary>
public sealed record RequirementSuggestion(string RequirementId, string SpecificationPath, string Title, RequirementSuggestionLevel Level, string Reason, double Score);

/// <summary>A test-case variant the evidence supports (a stated failure case, a condition clause of the expected outcome, a contract error
/// response). A proposal only: nothing is added until a tester adds it. <see cref="Expected"/> is null when the evidence states no outcome.</summary>
public sealed record TestCaseVariantProposal(string Key, string Title, string Dimension, string Basis, string? Preconditions, string? Action, string? Expected);

/// <summary>Is a translation service available, and if not, why (shown as is; writing the text by hand is always possible). <see cref="Source"/>
/// and <see cref="Targets"/> are the configured languages (default English → Norwegian bokmål).</summary>
public sealed record TranslationAvailability(bool Available, string? Provider, string Reason, string Source = "en", IReadOnlyList<string>? Targets = null)
{
    public IReadOnlyList<string> TargetLanguages => Targets is { Count: > 0 } t ? t : [ScenarioPlanning.Norwegian];
}

/// <summary>Translate the scenario and its cases (the chosen scenario only) into <see cref="Target"/> (null = the default target language).
/// <see cref="Overwrite"/> = also replace text a person wrote in that language.</summary>
public sealed record TranslateScenarioRequest(int BaseRevision, string ScenarioKey, bool Overwrite = false, string? Target = null);

/// <summary>What a translation request did, case by case. A translation never changes a review decision. <see cref="Provider"/> = the service
/// that translated (with an offline fallback, the one actually used).</summary>
public sealed record TranslateScenarioResult(TestPlanDraft? Draft, List<string> Translated, List<string> KeptPersonEdits, List<string> Failed, string Message,
    string? Target = null, string? Provider = null);

/// <summary>One scenario of a plan with its cases. <see cref="Primary"/> is the case prepared directly from the scenario (null when only cases written
/// under it remain). Key "" = cases no scenario is established for.</summary>
public sealed record ScenarioGroup(string Key, string Title, TestCaseDraft? Primary, List<TestCaseDraft> Cases)
{
    [JsonIgnore] public bool Unresolved => Key.Length == 0;
    [JsonIgnore] public int Approved => Cases.Count(c => c.Review == TestCaseReviewState.Accepted);
    [JsonIgnore] public int NeedReview => Cases.Count(c => c.Review is TestCaseReviewState.Generated or TestCaseReviewState.UnderReview or TestCaseReviewState.NeedsClarification);
    [JsonIgnore] public int NeedClarification => Cases.Count(c => c.Review == TestCaseReviewState.NeedsClarification);
}

public static partial class ScenarioPlanning
{
    public const string Norwegian = "nb";
    public const string NeedsVerificationMethod = "Needs clarification — verification method not identified";
    public const string UnresolvedTitle = "Written by hand — no scenario established";

    // ── Hierarchy ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plan's cases grouped by scenario, in plan order. A hand-written case without a stored scenario is never assigned to one: it is
    /// listed under the unresolved group (key "").</summary>
    public static List<ScenarioGroup> Groups(TestPlanDraft plan)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, List<TestCaseDraft>>(StringComparer.Ordinal);
        foreach (var c in plan.Cases)
        {
            var key = c.ParentScenario ?? "";
            if (!byKey.TryGetValue(key, out var list)) { byKey[key] = list = []; order.Add(key); }
            list.Add(c);
        }
        // A scenario whose own case is in the plan comes where that case is; the unresolved group comes last.
        return [.. order.OrderBy(k => k.Length == 0 ? 1 : 0).Select(k =>
        {
            var cases = byKey[k];
            var primary = k.Length == 0 ? null : cases.FirstOrDefault(c => c.Source.Kind != ScenarioSourceKind.Manual && c.Source.ScenarioId == k);
            var ordered = primary is null ? cases : [primary, .. cases.Where(c => !ReferenceEquals(c, primary))];
            var title = k.Length == 0 ? UnresolvedTitle : primary?.Generated.Title ?? cases.Select(c => c.ScenarioTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? k;
            return new ScenarioGroup(k, title, primary, ordered);
        })];
    }

    // ── Duplicate detection ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sibling cases that look like the same test (same purpose, preconditions and expected results in other words): proposals for the
    /// tester, never merged or removed. Threshold on word overlap of the comparable text.</summary>
    public static List<TestCaseDraft> LikelyDuplicates(string title, string? preconditions, IEnumerable<string?> expected, IEnumerable<TestCaseDraft> siblings, string? exceptCaseId = null)
    {
        var mine = Words($"{title} {preconditions} {string.Join(' ', expected)}");
        var myTitle = Words(title);
        if (mine.Count == 0) return [];
        static bool Same(HashSet<string> a, HashSet<string> b) => a.Count > 0 && b.Count > 0 && (double)a.Count(b.Contains) / Math.Max(a.Count, b.Count) >= 0.8;
        return [.. siblings.Where(s => s.Source.ScenarioId != exceptCaseId && s.Review != TestCaseReviewState.Excluded).Where(s =>
            Same(myTitle, Words(s.EffectiveTitle))
            || Same(mine, Words($"{s.EffectiveTitle} {s.EffectivePreconditions} {string.Join(' ', s.EffectiveStepExpected)} {s.EffectiveExpected}")))];
    }

    // ── Translation ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web);

    /// <summary>SHA-256 of the case's original text as the tester sees it (what a translation is made from).</summary>
    public static string SourceFingerprint(TestCaseDraft c) => Sha(JsonSerializer.Serialize(new
    {
        c.EffectiveTitle, c.EffectivePurpose, c.EffectivePreconditions, Steps = c.EffectiveSteps, Expected = c.EffectiveStepExpected, c.EffectiveExpected,
        Scenario = ScenarioFields(c), c.EffectiveObservation, c.EffectiveEnvironment, c.Edits.Roles, c.Edits.Dependencies,
    }, FingerprintJson));

    /// <summary>The scenario's Given / When / Then as its evidence states them (template values), for display and translation.</summary>
    public static (string? Given, string? When, string? Then) ScenarioFields(TestCaseDraft c) =>
        (c.Generated.Template.FirstOrDefault(t => t.Key == "given")?.Value, c.Generated.Template.FirstOrDefault(t => t.Key == "when")?.Value,
         c.Generated.Template.FirstOrDefault(t => t.Key == "then")?.Value);

    /// <summary>Fields after the steps: expected result, then scenario Given/When/Then, how to verify, environment, roles, dependencies.</summary>
    public const int FieldsAfterSteps = 8;

    public static TranslationState StateOf(TestCaseDraft c, string language = Norwegian) =>
        c.Translation(language) is not { } n ? TranslationState.None : n.SourceFingerprint == SourceFingerprint(c) ? TranslationState.Current : TranslationState.SourceChanged;

    /// <summary>A translation that covers every exported field: a title and the same number of steps (with results) as the original.</summary>
    public static bool Complete(TestCaseDraft c, string language = Norwegian)
    {
        if (c.Translation(language) is not { } n || string.IsNullOrWhiteSpace(n.Title) || n.Steps.Count != c.EffectiveSteps.Count || n.Steps.Any(string.IsNullOrWhiteSpace)) return false;
        if (!string.IsNullOrWhiteSpace(c.EffectivePreconditions) && string.IsNullOrWhiteSpace(n.Preconditions)) return false;
        var expected = c.EffectiveStepExpected;
        return expected.Select((e, i) => e is null || i < n.StepExpected.Count && !string.IsNullOrWhiteSpace(n.StepExpected[i])).All(ok => ok);
    }

    /// <summary>The text fields of a case to translate, in a fixed order (title, purpose, preconditions, each step, each step result, expected).</summary>
    public static List<string?> TranslatableFields(TestCaseDraft c)
    {
        var (given, when, then) = ScenarioFields(c);
        return [c.EffectiveTitle, c.EffectivePurpose, c.EffectivePreconditions, .. c.EffectiveSteps, .. c.EffectiveStepExpected, c.EffectiveExpected,
            given, when, then, c.EffectiveObservation, c.EffectiveEnvironment, c.Edits.Roles, c.Edits.Dependencies];
    }

    /// <summary>Builds the translation from the translated fields (same order as <see cref="TranslatableFields"/>); null if the count does not match.</summary>
    public static TestCaseTranslation? FromFields(TestCaseDraft c, IReadOnlyList<string?> fields, TranslationOrigin origin, string? provider, string by, DateTimeOffset at,
        string language = Norwegian)
    {
        var steps = c.EffectiveSteps.Count;
        if (fields.Count != 3 + 2 * steps + FieldsAfterSteps) return null;
        var after = 3 + 2 * steps;
        return new TestCaseTranslation
        {
            Title = fields[0], Purpose = fields[1], Preconditions = fields[2], Steps = [.. fields.Skip(3).Take(steps).Select(s => s ?? "")],
            StepExpected = [.. fields.Skip(3 + steps).Take(steps)], ExpectedResult = fields[after],
            Given = fields[after + 1], When = fields[after + 2], Then = fields[after + 3], ObservationMethod = fields[after + 4], Environment = fields[after + 5],
            Roles = fields[after + 6], Dependencies = fields[after + 7],
            SourceFingerprint = SourceFingerprint(c), Origin = origin, Provider = provider, By = by, At = at, Language = language,
        };
    }

    // Technical identifiers are replaced by numbered placeholders before a text is translated and put back afterwards, so they cannot be
    // translated: requirement and scenario ids, URLs, file paths, code in backticks, HTTP status codes, identifiers in code style (camelCase,
    // PascalCase with an inner capital, snake_case, dotted.names), enum-like UPPER_CASE words and quoted machine values.
    [GeneratedRegex(@"https?://\S+|`[^`]+`|""[A-Za-z0-9_.:/\-]+""|\b[A-Za-z]{1,10}-\d+[A-Za-z]?\b|(?:[\w.\-]+[/\\])+[\w.\-]+\.\w{1,6}\b|[\w\-]+\.(?:md|json|xml|xsd|cs|yaml|yml|graphql|ts|js|csv|xlsx)\b|\b[a-z]+(?:[A-Z][a-z0-9]*)+\b|\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\b|\b\w+_\w+\b|\b[a-zA-Z]\w*(?:\.[a-zA-Z]\w*){1,}\(?\)?|\b[1-5]\d\d\b|\b[A-Z][A-Z0-9]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalToken();

    private static readonly HashSet<string> PlainUpper = new(StringComparer.Ordinal) { "MUST", "SHALL", "SHOULD", "NOT", "MAY", "AND", "THE", "WHEN", "THEN", "GIVEN", "NOTE" };

    /// <summary>Replaces technical identifiers with ⟦n⟧ placeholders; <see cref="Restore"/> puts them back.</summary>
    public static (string Masked, List<string> Tokens) Protect(string text)
    {
        var tokens = new List<string>();
        var masked = TechnicalToken().Replace(text, m =>
        {
            if (PlainUpper.Contains(m.Value)) return m.Value;
            tokens.Add(m.Value);
            return $"⟦{tokens.Count - 1}⟧";
        });
        return (masked, tokens);
    }

    /// <summary>The translated text with every identifier restored; null when a placeholder is missing, repeated or invented (the translation
    /// then is not used, so an identifier can never be lost or changed).</summary>
    public static string? Restore(string translated, IReadOnlyList<string> tokens)
    {
        var found = Regex.Matches(translated, "⟦(\\d+)⟧").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (found.Count != tokens.Count || found.Distinct().Count() != tokens.Count || found.Any(i => i >= tokens.Count)) return null;
        return Regex.Replace(translated, "⟦(\\d+)⟧", m => tokens[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    /// <summary>The exported text of a case in the chosen language: translated only when the case has a complete text in that language (all
    /// fields from one language, never mixed), else the original with the reason. <c>Translated</c> = the text is in <paramref name="language"/>.</summary>
    public static (string Title, string? Preconditions, List<string> Steps, List<string?> StepExpected, bool Translated, string? Note) Localized(TestCaseDraft c, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return (c.EffectiveTitle, c.EffectivePreconditions, c.EffectiveSteps, c.EffectiveStepExpected, false, null);
        var name = TranslationLanguages.Name(language);
        if (!Complete(c, language)) return (c.EffectiveTitle, c.EffectivePreconditions, c.EffectiveSteps, c.EffectiveStepExpected, false,
            c.Translation(language) is null ? $"No {name} text: exported in the original language." : $"The {name} text does not cover every step: exported in the original language.");
        var n = c.Translation(language)!;
        var expected = c.EffectiveStepExpected;
        return (n.Title!, string.IsNullOrWhiteSpace(n.Preconditions) ? null : n.Preconditions, n.Steps,
            [.. n.Steps.Select((_, i) => expected[i] is null ? null : n.StepExpected[i])],
            true, StateOf(c, language) == TranslationState.SourceChanged ? $"The {name} text was made from an older original: review it." : null);
    }

    // ── Requirement suggestions ─────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\b[A-Z]{1,6}-\d+[a-z]?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RequirementIdPattern();

    /// <summary>The scenario text requirement matching reads: title, purpose, starting situation, actions and outcomes.</summary>
    public static string ScenarioText(TestCaseDraft c) => string.Join("\n", new[]
    {
        c.EffectiveTitle, c.EffectivePurpose, c.EffectivePreconditions, string.Join("\n", c.EffectiveSteps), string.Join("\n", c.EffectiveStepExpected), c.EffectiveExpected,
        string.Join("\n", c.Generated.Template.Select(t => t.Value)),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// Suggests requirements of the project's specification for a scenario or case. A: the scenario names the requirement id (its evidence or its
    /// text). B: the requirement's wording names the scenario's acceptance scenario or user story (the specification's own structure). C: both
    /// state the same behaviour — distinctive words and phrases they share, weighted by how rare they are in the specification, above a firm
    /// threshold, explained by the shared words. D: weaker overlap, shown only as possible. Linked and rejected requirements are left out; strongest first.
    /// </summary>
    public static List<RequirementSuggestion> SuggestRequirements(string scenarioText, IEnumerable<string> explicitIds, IEnumerable<string> structuralRefs,
        IReadOnlyCollection<ProjectRequirementOption> requirements, IEnumerable<string>? excludedKeys = null, int maxSemantic = 3, int maxWeak = 3)
    {
        var excluded = new HashSet<string>(excludedKeys ?? [], StringComparer.Ordinal);
        var ids = new HashSet<string>(explicitIds.Concat(RequirementIdPattern().Matches(scenarioText).Select(m => m.Value)).Select(NormId), StringComparer.Ordinal);
        var refs = structuralRefs.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<RequirementSuggestion>();
        var candidates = requirements.Where(r => !excluded.Contains(Key(r.RequirementId, r.SpecificationPath))).ToList();

        foreach (var r in candidates.Where(r => ids.Contains(NormId(r.RequirementId))))
            result.Add(new(r.RequirementId, r.SpecificationPath, r.Title, RequirementSuggestionLevel.Explicit, $"The scenario names {r.RequirementId}.", 100));
        foreach (var r in candidates.Where(r => !ids.Contains(NormId(r.RequirementId))))
            if (refs.FirstOrDefault(x => Regex.IsMatch(r.Title, $@"(^|[^\w-]){Regex.Escape(x)}($|[^\w-])", RegexOptions.IgnoreCase)) is { } named)
                result.Add(new(r.RequirementId, r.SpecificationPath, r.Title, RequirementSuggestionLevel.Structural, $"The requirement's wording refers to {named}, which this scenario belongs to.", 90));

        var taken = result.Select(s => Key(s.RequirementId, s.SpecificationPath)).ToHashSet(StringComparer.Ordinal);
        var rest = candidates.Where(r => !taken.Contains(Key(r.RequirementId, r.SpecificationPath))).ToList();
        if (rest.Count == 0) return result;

        // Rarity of each word across the whole specification (rare shared words say more than common ones).
        var docs = requirements.Select(r => Words(r.Title)).ToList();
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in docs) foreach (var w in d) df[w] = df.GetValueOrDefault(w) + 1;
        double Idf(string w) => Math.Log((docs.Count + 1.0) / (df.GetValueOrDefault(w) + 1.0)) + 0.1;
        var scenarioWords = Words(scenarioText);
        var scenarioPhrases = Phrases(scenarioText);
        if (scenarioWords.Count == 0) return result;
        var maxPossible = scenarioWords.Sum(Idf);

        var scored = new List<(ProjectRequirementOption R, double Score, List<string> Shared, List<string> SharedPhrases)>();
        foreach (var r in rest)
        {
            var words = Words(r.Title);
            var shared = scenarioWords.Where(words.Contains).OrderByDescending(Idf).ToList();
            if (shared.Count == 0) continue;
            var phrases = Phrases(r.Title).Where(scenarioPhrases.Contains).ToList();
            var score = (shared.Sum(Idf) + 1.5 * phrases.Count) / maxPossible;
            // The reason quotes the requirement's own words (never the stems matching works on).
            var surface = Surface(r.Title);
            scored.Add((r, score, [.. shared.Select(w => surface.GetValueOrDefault(w, w))], [.. phrases.Select(ph => surface.GetValueOrDefault(ph, ph))]));
        }
        foreach (var (r, score, shared, phrases) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.R.RequirementId, StringComparer.OrdinalIgnoreCase))
        {
            var semantic = score >= 0.30 && phrases.Count >= 1 && shared.Count >= 3;
            var weak = !semantic && score >= 0.18 && shared.Count >= 2;
            if (semantic && result.Count(x => x.Level == RequirementSuggestionLevel.Semantic) < maxSemantic)
                result.Add(new(r.RequirementId, r.SpecificationPath, r.Title, RequirementSuggestionLevel.Semantic,
                    $"Both state the same behaviour: they share {Quote(phrases.Take(3))}{(shared.Count > 0 ? $" and the words {Quote(shared.Take(5))}" : "")}.", Math.Round(score, 3)));
            else if ((semantic || weak) && result.Count(x => x.Level == RequirementSuggestionLevel.Weak) < maxWeak)
                result.Add(new(r.RequirementId, r.SpecificationPath, r.Title, RequirementSuggestionLevel.Weak,
                    $"Possibly related: they share the words {Quote(shared.Take(5))}{(phrases.Count > 0 ? $" and {Quote(phrases.Take(2))}" : "")}. Not established — check the wording.", Math.Round(score, 3)));
        }
        return result;
    }

    /// <summary>The user-story and acceptance-scenario references a scenario belongs to, read from its evidence (e.g. "US6", "User Story 6",
    /// "scenario-42"): what a requirement's wording must name to be a structural (B) suggestion.</summary>
    public static List<string> StructuralReferences(TestCaseDraft c)
    {
        var refs = new List<string>();
        // The user story the scenario is written under (from the document structure), by its number: "US6", "User Story 6".
        if (c.Generated.Story?.StoryId is { } storyId)
        {
            refs.Add(storyId);
            if (Regex.Match(storyId, @"^US(\d+)$", RegexOptions.IgnoreCase) is { Success: true } n) refs.Add($"User Story {n.Groups[1].Value}");
        }
        foreach (var e in c.Generated.Evidence.Where(e => e.Kind == "specification"))
        {
            var anchor = e.Reference.Contains('#') ? e.Reference[(e.Reference.LastIndexOf('#') + 1)..] : null;
            if (string.IsNullOrWhiteSpace(anchor)) continue;
            refs.Add(anchor);
            if (Regex.Match(anchor, @"^US(\d+)", RegexOptions.IgnoreCase) is { Success: true } us) { refs.Add($"US{us.Groups[1].Value}"); refs.Add($"User Story {us.Groups[1].Value}"); }
        }
        return refs;
    }

    public static string Key(string id, string? path) => $"{NormId(id)}|{(path ?? "").Replace('\\', '/').Trim().TrimStart('/').ToUpperInvariant()}";
    private static string NormId(string id) => id.Trim().ToUpperInvariant();
    private static string Quote(IEnumerable<string> items) => string.Join(", ", items.Select(i => $"\"{i}\""));

    // ── Test-case variants from evidence ─────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"[;]\s+|,\s+(?=(?:even|unless|except|but|if|when|otherwise)\b)|\.\s+(?=(?:If|When|Otherwise|Unless|Even)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseSplit();

    /// <summary>
    /// Variants the scenario's own evidence supports: each stated failure case, each further condition of the expected outcome (a clause such as
    /// "even with …", "if it covers only part …"), and each additional contract error response. Nothing is proposed from guesswork (no generic
    /// "negative/boundary" variants without evidence), and a proposal whose title already exists under the scenario is left out.
    /// </summary>
    public static List<TestCaseVariantProposal> Variants(TestCaseDraft scenario, IEnumerable<TestCaseDraft> siblings)
    {
        var list = new List<TestCaseVariantProposal>();
        var expected = scenario.Generated.ExpectedResult;
        if (!string.IsNullOrWhiteSpace(expected))
        {
            var clauses = ClauseSplit().Split(expected).Select(s => s.Trim().TrimEnd('.')).Where(s => s.Length > 8).ToList();
            foreach (var clause in clauses.Skip(1))
            {
                var condition = Regex.Match(clause, @"^(even (?:with|if|when)|if|when|unless|except (?:when|if)|otherwise)\b\s*(.*)$", RegexOptions.IgnoreCase);
                var title = condition.Success ? $"{scenario.Generated.Title} — {Upper(condition.Value)}" : $"{scenario.Generated.Title} — {Upper(clause)}";
                list.Add(new($"clause:{Sha(clause)[..10]}", Clip(title, 200), "Business-rule combination",
                    $"The expected outcome states a further condition: \"{clause}\".", condition.Success ? Upper(condition.Groups[2].Value) : null, null, Upper(clause) + "."));
            }
        }
        foreach (var f in scenario.Generated.FailureCases.Where(f => !string.IsNullOrWhiteSpace(f)))
            list.Add(new($"failure:{Sha(f)[..10]}", Clip($"{scenario.Generated.Title} — {f}", 200), "Negative", $"The evidence names the failure case \"{f}\".", null, null, null));
        var steps = scenario.Generated.Steps;
        for (var i = 1; i < steps.Count; i++)
        {
            if (!Regex.IsMatch(steps[i], @"\b(without|invalid|missing|wrong|not allowed|unauthori[sz]ed|forbidden)\b", RegexOptions.IgnoreCase)) continue;
            var result = i < scenario.Generated.StepExpected.Count ? scenario.Generated.StepExpected[i] : null;
            list.Add(new($"step:{Sha(steps[i])[..10]}", Clip($"{scenario.Generated.Title} — {steps[i]}", 200), "Negative", $"The contract states this error path: \"{steps[i]}\".", null, steps[i], result));
        }
        var titles = siblings.Select(s => s.EffectiveTitle.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. list.Where(v => !titles.Contains(v.Title)).DistinctBy(v => v.Key)];
    }

    // ── Words ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "are", "was", "were", "has", "have", "had", "not", "but", "any", "all", "its", "their",
        "they", "them", "then", "when", "given", "must", "shall", "should", "may", "can", "will", "would", "been", "being", "each", "every", "which", "who",
        "what", "where", "how", "than", "also", "only", "one", "two", "per", "via", "such", "other", "more", "most", "same", "there", "here", "after", "before",
        "system", "service", "user", "data", "case", "test", "scenario", "requirement", "outcome", "result", "expected", "confirm", "check", "verify", "step",
        "og", "det", "som", "med", "for", "til", "den", "ikke", "skal", "kan", "har", "fra", "eller", "når", "ved", "alle", "blir",
    };

    private static HashSet<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}][\p{L}\p{Nd}]{2,}").Select(m => Stem(m.Value)).Where(w => w.Length >= 3 && !Stop.Contains(w))];

    /// <summary>Stem (and stemmed two-word phrase) → the words as written in <paramref name="text"/> (first occurrence, lower case).</summary>
    private static Dictionary<string, string> Surface(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return map;
        var words = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}][\p{L}\p{Nd}]{2,}").Select(m => (Stem: Stem(m.Value), Word: m.Value)).Where(w => !Stop.Contains(w.Stem)).ToList();
        foreach (var w in words) map.TryAdd(w.Stem, w.Word);
        foreach (var (a, b) in words.Zip(words.Skip(1))) map.TryAdd($"{a.Stem} {b.Stem}", $"{a.Word} {b.Word}");
        return map;
    }

    private static HashSet<string> Phrases(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var words = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}][\p{L}\p{Nd}]{2,}").Select(m => Stem(m.Value)).Where(w => !Stop.Contains(w)).ToList();
        return [.. words.Zip(words.Skip(1)).Select(p => $"{p.First} {p.Second}")];
    }

    private static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) return w[..^3] + "y";
        if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) return w[..^1];
        return w;
    }

    private static string Upper(string text) => text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
