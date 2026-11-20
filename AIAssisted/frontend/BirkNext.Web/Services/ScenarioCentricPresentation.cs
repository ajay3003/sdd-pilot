using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Plan &amp; prepare by scenario: the words and small decisions of the scenario → test case view (counts, requirement suggestions, the Norwegian
/// text, the testability checklist and the separate lifecycle states). Pure; no I/O. The suggestion and translation rules themselves live in
/// <see cref="ScenarioPlanning"/> (shared with the server), never here.
/// </summary>
public static class ScenarioCentricPresentation
{
    /// <summary>"3 test cases · 1 approved · 2 need review" (only the parts that are not zero, besides the count).</summary>
    public static string Counts(ScenarioGroup g)
    {
        var parts = new List<string> { $"{g.Cases.Count} test case{(g.Cases.Count == 1 ? "" : "s")}" };
        if (g.Approved > 0) parts.Add($"{g.Approved} approved");
        if (g.NeedClarification > 0) parts.Add($"{g.NeedClarification} need{(g.NeedClarification == 1 ? "s" : "")} clarification");
        var review = g.NeedReview - g.NeedClarification;
        if (review > 0) parts.Add($"{review} need{(review == 1 ? "s" : "")} review");
        return string.Join(" · ", parts);
    }

    /// <summary>Rows for a text field that shows its whole text: about 70 characters per row, 1–6 rows (the field still resizes by hand).</summary>
    public static int Rows(string? text) => string.IsNullOrEmpty(text) ? 1
        : Math.Clamp(text.Split('\n').Sum(line => 1 + line.Length / 70), 1, 6);

    /// <summary>
    /// A readable evidence reference: "spec.md#scenario-3" for "Project/SkoleService/specs/001-skoleservice/spec.md#scenario-3" — the file and its
    /// anchor, the folders in the tooltip. References without a path ("skolenaervaer/duplikat-melding") keep their last two parts.
    /// </summary>
    public static string ShortReference(string reference)
    {
        var text = reference.Trim();
        var hash = text.IndexOf('#');
        var path = hash >= 0 ? text[..hash] : text;
        var anchor = hash >= 0 ? text[hash..] : "";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return text;
        var last = parts[^1];
        return (last.Contains('.') ? last : $"{parts[^2]}/{last}") + anchor;
    }

    /// <summary>The first 8 characters of a GUID-like id ("025eb58c"), the whole id in the tooltip.</summary>
    public static string ShortId(string id) => id.Length > 12 ? id.Replace("-", "")[..8] : id;

    /// <summary>The scenario's identifier as the source names it ("acceptance scenario US1-2", "spec.md#scenario-42"), never an internal key.</summary>
    public static string? SourceReference(TestCaseDraft? primary) =>
        primary?.Generated.Evidence.FirstOrDefault(e => e.Kind is "specification" or "requirement")?.Reference ?? primary?.Generated.Evidence.FirstOrDefault()?.Reference;

    /// <summary>The starting situation / action / outcome of a scenario as its evidence states them (Given / When / Then), when stated.</summary>
    public static List<(string Label, string Value)> Overview(TestCaseDraft primary)
    {
        var list = new List<(string, string)>();
        foreach (var t in primary.Generated.Template.Where(t => !string.IsNullOrWhiteSpace(t.Value)))
            list.Add((t.Key switch { "given" => "Starting situation", "when" => "Trigger or action", "then" => "Expected business outcome", _ => t.Label }, t.Value!));
        return list;
    }

    /// <summary>The overview's Given / When / Then with a key per field (given, when, then), for showing them in another language.</summary>
    public static List<(string Label, string Key, string Value)> OverviewFields(TestCaseDraft primary) =>
        [.. primary.Generated.Template.Where(t => !string.IsNullOrWhiteSpace(t.Value) && t.Key is "given" or "when" or "then")
            .Select(t => (t.Key switch { "given" => "Starting situation", "when" => "Trigger or action", _ => "Expected business outcome" }, t.Key, t.Value!))];

    public static string? TranslatedOverview(TestCaseTranslation t, string key) => key switch
    {
        "given" => t.Given, "when" => t.When, "then" => t.Then, _ => null,
    } is { Length: > 0 } v ? v : null;

    /// <summary>What Translate all will do before anything is sent: scenarios and cases it covers, and text a person wrote that it keeps.</summary>
    public sealed record TranslateAllPreview(int Scenarios, int Cases, int KeptPersonText, int AlreadyCurrent, int Stories);

    public static TranslateAllPreview PreviewTranslateAll(TestPlanDraft plan, string language, bool overwrite)
    {
        var cases = plan.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).ToList();
        var person = cases.Count(c => c.Translation(language) is { Origin: TranslationOrigin.Manual or TranslationOrigin.MachineEdited });
        var current = cases.Count(c => ScenarioPlanning.StateOf(c, language) == TranslationState.Current && c.Translation(language)!.Origin == TranslationOrigin.Machine);
        return new(cases.Select(c => c.ParentScenario ?? "").Distinct().Count(), cases.Count - (overwrite ? 0 : person), overwrite ? 0 : person, current,
            cases.Select(c => c.Generated.Story?.Key).OfType<string>().Distinct().Count());
    }

    // ── Feature → User Story → Scenario ──────────────────────────────────────────────────────────────────────────────

    public enum StoryGroupKind { Story, NoStory, OtherSources, Unresolved }

    /// <summary>One group of scenarios: a user story (with its feature), specification scenarios under no story, other sources, or hand-written cases.</summary>
    public sealed record StoryGroup(StoryGroupKind Kind, string Key, string Label, string? Feature, BirkNext.TestCoverage.CoverageUserStory? Story, List<ScenarioGroup> Scenarios);

    /// <summary>The story a scenario belongs to: from its own case (prepared from the specification), else from any case written under it.</summary>
    public static BirkNext.TestCoverage.CoverageUserStory? StoryOf(ScenarioGroup g) => g.Primary?.Generated.Story ?? g.Cases.Select(c => c.Generated.Story).FirstOrDefault(s => s is not null);

    /// <summary>
    /// The plan's scenarios grouped by the user story the specification writes them under, stories in document order per feature; then
    /// specification scenarios with no story identified ("Parent user story: not identified"), scenarios from other sources (UI, contracts …),
    /// and hand-written cases without a scenario. Null when no scenario has a story: the plain scenario list is shown then.
    /// </summary>
    public static List<StoryGroup>? StoryGroups(List<ScenarioGroup> groups)
    {
        if (!groups.Any(g => StoryOf(g) is not null)) return null;
        var result = new List<StoryGroup>();
        foreach (var feature in groups.Where(g => StoryOf(g) is not null).GroupBy(g => StoryOf(g)!.SpecificationPath ?? "", StringComparer.Ordinal))
            foreach (var story in feature.GroupBy(g => StoryOf(g)!.Key, StringComparer.Ordinal).OrderBy(x => StoryOf(x.First())!.Ordinal))
            {
                var s = StoryOf(story.First())!;
                result.Add(new(StoryGroupKind.Story, s.Key, StoryLabel(s), FeatureLabel(s), s, [.. story])); // the plan's order: the specification's unless a tester reordered
            }
        var rest = groups.Where(g => StoryOf(g) is null).ToList();
        var noStory = rest.Where(g => !g.Unresolved && g.Primary?.Generated.Capability == "specification").ToList();
        var other = rest.Where(g => !g.Unresolved && g.Primary?.Generated.Capability != "specification").ToList();
        if (noStory.Count > 0) result.Add(new(StoryGroupKind.NoStory, "no-story", "Scenarios not under a user story", null, null, noStory));
        if (other.Count > 0) result.Add(new(StoryGroupKind.OtherSources, "other-sources", "Scenarios from other sources", null, null, other));
        if (rest.FirstOrDefault(g => g.Unresolved) is { } unresolved) result.Add(new(StoryGroupKind.Unresolved, "unresolved", ScenarioPlanning.UnresolvedTitle, null, null, [unresolved]));
        return result;
    }

    /// <summary>"User Story 6 — Expected population and coverage breakdown" as written (the heading text without its priority suffix).</summary>
    public static string StoryLabel(BirkNext.TestCoverage.CoverageUserStory s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Title, @"\s*\((?:Priority|Prioritet)\s*:\s*P\d+\s*\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    /// <summary>The feature a story belongs to: its module and specification folder ("SkoleService · 001-skoleservice").</summary>
    public static string? FeatureLabel(BirkNext.TestCoverage.CoverageUserStory s)
    {
        if (s.SpecificationPath is not { Length: > 0 } path) return s.Module;
        var parts = path.Replace('\\', '/').Split('/');
        var feature = parts.Length >= 2 ? parts[^2] : null;
        var module = s.Module ?? (parts.Length >= 4 && parts[^3] == "specs" ? parts[^4] : null);
        return string.Join(" · ", new[] { module, feature }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ── Requirement suggestions ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Suggested requirements for a case: its own text (and, for a case written under a scenario, the scenario's), the ids its evidence names and
    /// the scenario's structural references, against the project's requirement list. Requirements already linked (automatically or by hand) and
    /// suggestions the tester rejected for this case are left out.
    /// </summary>
    public static List<RequirementSuggestion> Suggestions(TestCaseDraft c, TestCaseDraft? scenario, IReadOnlyCollection<ProjectRequirementOption>? options)
    {
        if (options is not { Count: > 0 }) return [];
        var source = scenario ?? c;
        var text = ScenarioPlanning.ScenarioText(c) + (scenario is not null && !ReferenceEquals(scenario, c) ? "\n" + ScenarioPlanning.ScenarioText(scenario) : "");
        var linkedIds = c.RequirementLinks.Select(l => l.RequirementId.Trim().ToUpperInvariant())
            .Concat(RequirementLinkPresentation.IsSpecificationCase(c) ? c.Generated.Requirements.Select(r => r.RuleId.Trim().ToUpperInvariant()) : [])
            .Concat(scenario?.RequirementLinks.Where(l => l.Scope == RequirementLinkScope.Scenario).Select(l => l.RequirementId.Trim().ToUpperInvariant()) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var excluded = options.Where(o => linkedIds.Contains(o.RequirementId.Trim().ToUpperInvariant())).Select(o => ScenarioPlanning.Key(o.RequirementId, o.SpecificationPath))
            .Concat((c.RejectedSuggestions ?? []).Select(r => ScenarioPlanning.Key(r.RequirementId, r.SpecificationPath)));
        var explicitIds = source.Generated.Requirements.Select(r => r.RuleId).Concat(c.Generated.Requirements.Select(r => r.RuleId));
        return ScenarioPlanning.SuggestRequirements(text, explicitIds, ScenarioPlanning.StructuralReferences(source), options, excluded);
    }

    public static string LevelLabel(RequirementSuggestionLevel level) => level switch
    {
        RequirementSuggestionLevel.Explicit => "A · Named in the scenario",
        RequirementSuggestionLevel.Structural => "B · Same specification structure",
        RequirementSuggestionLevel.Semantic => "C · Same behaviour described",
        _ => "D · Possibly related",
    };

    public static string LevelTone(RequirementSuggestionLevel level) => level switch
    {
        RequirementSuggestionLevel.Explicit or RequirementSuggestionLevel.Structural => "ok",
        RequirementSuggestionLevel.Semantic => "info",
        _ => "quiet",
    };

    /// <summary>Confirms a suggestion as a link of the case (scope null) or of the scenario as a whole (stored on the scenario's own case, scope
    /// Scenario, never counted as test-design coverage). Who and when are recorded by the server on save.</summary>
    public static TestCaseDraft Confirm(TestCaseDraft c, RequirementSuggestion s, string? scope)
    {
        var option = new ProjectRequirementOption(s.RequirementId, s.Title, s.SpecificationPath);
        var linked = RequirementLinkPresentation.Link(c, option);
        var key = RequirementLinkPresentation.Key(s.RequirementId, s.SpecificationPath);
        return linked with
        {
            RequirementLinks = [.. linked.RequirementLinks.Select(l => RequirementLinkPresentation.Key(l.RequirementId, l.SpecificationPath) == key && l.LinkedAt == default
                ? l with { Scope = scope, SuggestionLevel = s.Level, SuggestionReason = s.Reason } : l)],
        };
    }

    /// <summary>Rejects a suggestion for this case only (saved with the draft; it is not proposed again for the case).</summary>
    public static TestCaseDraft Reject(TestCaseDraft c, RequirementSuggestion s) => c with
    {
        RejectedSuggestions = [.. c.RejectedSuggestions ?? [], new RejectedRequirementSuggestion { RequirementId = s.RequirementId, SpecificationPath = s.SpecificationPath }],
    };

    // ── Translated text (per language; default Norwegian bokmål) ─────────────────────────────────────────────────────

    public static string TranslationLabel(TestCaseDraft c, string language)
    {
        var name = TranslationLanguages.Name(language);
        return ScenarioPlanning.StateOf(c, language) switch
        {
            TranslationState.None => $"No {name} text",
            TranslationState.SourceChanged => $"{name} text made from an older original — review it",
            _ => c.Translation(language)!.Origin switch
            {
                TranslationOrigin.Machine => $"Machine translation{(c.Translation(language)!.Provider is { } p ? $" ({p})" : "")} — not reviewed",
                TranslationOrigin.MachineEdited => "Machine translation, edited by a person",
                _ => $"Written in {name} by a person",
            },
        };
    }

    public static string TranslationTone(TestCaseDraft c, string language) => ScenarioPlanning.StateOf(c, language) switch
    {
        TranslationState.None => "quiet",
        TranslationState.SourceChanged => "warn",
        _ => c.Translation(language)!.Origin == TranslationOrigin.Machine ? "info" : "ok",
    };

    /// <summary>All languages a case has text in, in words ("Norwegian (bokmål)", "Swedish: review"), or "None".</summary>
    public static string Languages(TestCaseDraft c) => c.TranslatedLanguages.Any()
        ? string.Join(" · ", c.TranslatedLanguages.Select(l => TranslationLanguages.Name(l) + (ScenarioPlanning.StateOf(c, l) == TranslationState.SourceChanged ? ": review" : "")))
        : "None";

    /// <summary>The text to edit in <paramref name="language"/>: the saved one, or an empty one with a slot per step (nothing is pre-filled from the original).</summary>
    public static TestCaseTranslation Editable(TestCaseDraft c, string language)
    {
        var n = c.Translation(language) ?? new TestCaseTranslation { Language = language };
        var count = c.EffectiveSteps.Count;
        return n with
        {
            Language = language,
            Steps = [.. Enumerable.Range(0, count).Select(i => i < n.Steps.Count ? n.Steps[i] : "")],
            StepExpected = [.. Enumerable.Range(0, count).Select(i => i < n.StepExpected.Count ? n.StepExpected[i] : null)],
        };
    }

    public static TestCaseDraft WithTranslation(TestCaseDraft c, string language, Func<TestCaseTranslation, TestCaseTranslation> change) =>
        c.WithTranslation(language, change(Editable(c, language)));

    /// <summary>
    /// A requirement's wording for display: the specification's list markup in front of it ("- **FR-027**: …", "**FR-027** – …") and bold
    /// markers removed, so the id is not shown twice. The text itself is unchanged.
    /// </summary>
    public static string RequirementWording(string? title, string requirementId)
    {
        var text = (title ?? "").Trim();
        var lead = new System.Text.RegularExpressions.Regex(@"^[-*•]?\s*\**\s*" + System.Text.RegularExpressions.Regex.Escape(requirementId) + @"\s*\**\s*[:–—-]?\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return lead.Replace(text, "").Replace("**", "").Trim();
    }

    public static TestCaseTranslation SetStep(TestCaseTranslation n, int index, string? action, string? expected)
    {
        var steps = n.Steps.ToList();
        var results = n.StepExpected.ToList();
        while (steps.Count <= index) steps.Add("");
        while (results.Count <= index) results.Add(null);
        if (action is not null) steps[index] = action;
        if (expected is not null) results[index] = expected.Length == 0 ? null : expected;
        return n with { Steps = steps, StepExpected = results };
    }

    // ── Testability checklist and lifecycle ─────────────────────────────────────────────────────────────────────────

    public enum Check { Ready, NeedsAction, NotAssessed, NotApplicable }

    public sealed record ChecklistItem(string Id, string Label, Check State, string Detail);

    /// <summary>
    /// The case's testability, one fact per line. Ready only for what the case states (never because it was approved); environment and access are
    /// "Not assessed" until the tester describes them — BirkNext cannot verify an environment from here.
    /// </summary>
    public static List<ChecklistItem> Checklist(TestCaseDraft c)
    {
        static Check Stated(string? v) => string.IsNullOrWhiteSpace(v) ? Check.NotAssessed : Check.Ready;
        var steps = c.EffectiveSteps;
        var open = c.EffectiveStepExpected.Count(e => e is null);
        var data = DataState(c);
        return
        [
            new("environment", "Test environment identified", Stated(c.EffectiveEnvironment), c.EffectiveEnvironment ?? "Not described (More fields → Environment and prerequisites)."),
            new("access", "Required access identified", Stated(c.Edits.Roles), c.Edits.Roles ?? "Not described (More fields → Roles and access)."),
            new("data", "Test data", data.Item1, data.Item2),
            new("execution", "Execution mechanism known", steps.Count == 0 ? Check.NeedsAction : Check.Ready, steps.Count == 0 ? "No steps yet." : $"{steps.Count} step{(steps.Count == 1 ? "" : "s")}."),
            new("expected", "Expected outcome defined", steps.Count > 0 && open == 0 && !string.IsNullOrWhiteSpace(c.EffectiveExpected) ? Check.Ready : Check.NeedsAction,
                open > 0 ? $"{open} step{(open == 1 ? "" : "s")} without an expected result." : string.IsNullOrWhiteSpace(c.EffectiveExpected) ? "The case's expected result is not stated." : "Every step has an expected result."),
            new("observation", "Observation mechanism known", string.IsNullOrWhiteSpace(c.EffectiveObservation) ? Check.NeedsAction : Check.Ready,
                c.EffectiveObservation ?? ScenarioPlanning.NeedsVerificationMethod),
            new("dependencies", "Dependencies identified", Stated(c.Edits.Dependencies), c.Edits.Dependencies ?? "Not described (More fields → Dependencies)."),
        ];
    }

    /// <summary>Test Data of a case: Available / Required but missing / Needs clarification (a linked asset is unsuitable) / Not required / Not assessed.</summary>
    public static (Check State, string Detail) DataState(TestCaseDraft c)
    {
        var need = c.Edits.TestDataNeed is { } n && n != TestDataNeed.Unknown ? n
            : c.Generated.TestDataNeed != TestDataNeed.Unknown ? c.Generated.TestDataNeed
            : !string.IsNullOrWhiteSpace(c.Generated.RequiredTestData) ? TestDataNeed.Required : TestDataNeed.Unknown;
        if (need == TestDataNeed.NotRequired) return (Check.NotApplicable, "Not required (a reviewer decided).");
        if (c.TestData.Count > 0)
            return c.TestData.All(d => d.Resolved && d.Approval == "Approved")
                ? (Check.Ready, $"Available: {string.Join(", ", c.TestData.Select(d => d.Alias.Length > 0 ? d.Alias : d.AssetId))}.")
                : (Check.NeedsAction, "Needs clarification: a linked Test Data asset is not approved or no longer exists.");
        return need == TestDataNeed.Required
            ? (Check.NeedsAction, $"Required but missing{(c.Generated.RequiredTestData is { Length: > 0 } what ? $": {what}" : "")}. Link it in Test Data.")
            : (Check.NotAssessed, "Not assessed: decide whether this case needs Test Data.");
    }

    public static string CheckLabel(Check c) => c switch { Check.Ready => "Ready", Check.NeedsAction => "Needs action", Check.NotApplicable => "Not applicable", _ => "Not assessed" };
    public static string CheckTone(Check c) => c switch { Check.Ready => "ok", Check.NeedsAction => "warn", _ => "quiet" };

    /// <summary>Case content: Unsaved changes / Saved (never edited by a person) / Revised (a person changed it), independent of the review decision.</summary>
    public static string ContentState(TestCaseDraft c, bool unsaved) => unsaved ? "Unsaved changes" : c.EditHistory.Count > 0 ? "Saved · revised" : "Saved";

    /// <summary>The export fact BirkNext knows: an Azure DevOps id a reader verified, or none. Exporting a file is never execution.</summary>
    public static string ExportState(TestCaseDraft c) =>
        c.VerifiedAzureDevOpsTestCaseId is { Length: > 0 } id ? $"Azure DevOps id {id} known" : "No Azure DevOps id known";

    /// <summary>"Generated — not reviewed" for a case whose content came from the evidence and nobody has reviewed yet.</summary>
    public static string? GeneratedNotReviewed(TestCaseDraft c) =>
        c.Source.Kind != ScenarioSourceKind.Manual && c.Review == TestCaseReviewState.Generated && c.ReviewHistory.Count == 0 ? "Generated — not reviewed" : null;
}
