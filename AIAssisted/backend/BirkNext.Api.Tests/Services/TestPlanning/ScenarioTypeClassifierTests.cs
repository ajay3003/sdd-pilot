using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Acceptance scenario type (2026-11-26): functional / non-functional / mixed / unclassified per acceptance scenario, from what the scenario itself
/// asserts. Every Given/When/Then below is verbatim from the real M2LB package (M2LB.zip): the source path and scenario are named per case.
/// </summary>
public sealed class ScenarioTypeClassifierTests
{
    private static DiscoveredScenario Ac(string given, string when, string then, string title = "", params string[] requirements) => new()
    {
        CandidateId = "c", CapabilityId = "specification", CanonicalKey = "scenario:m|s", Title = title.Length > 0 ? title : when, ExpectedBehaviour = then,
        Template = [new("given", "Given", given), new("when", "When", when), new("then", "Then", then)], RequirementIds = [.. requirements], Fingerprint = "f1",
    };

    private static ScenarioTypeResult Classify(string given, string when, string then) => ScenarioTypeClassifier.Classify(Ac(given, when, then));

    [Theory] // Case A — Frontend/specs/002-child-search-profile/spec.md, User Story 1 «Search for a Child», scenarios 1–6.
    [InlineData("the caseworker is on the child search page", "they type a child's name in the search field", "a list of matching children appears with name, age, birth date, and current case status")]
    [InlineData("the caseworker is on the child search page", "they type a BiRK-ID in the search field", "the matching child appears in the results")]
    [InlineData("the caseworker has entered a search term", "they press the clear (×) button", "the search input is cleared and the results are reset")]
    [InlineData("no children match the search term", "the search completes", "the message \"Ingen barn funnet\" is displayed")]
    [InlineData("the caseworker is on the search page without having searched", "the page loads", "a \"Nylig åpnet\" (recently opened) list and a \"Barn i avdelingen\" (children in the department) list are shown")]
    [InlineData("the caseworker has active search results displayed", "they press the clear (×) button", "the search results are removed and \"Nylig åpnet\" and \"Barn i avdelingen\" become visible again")]
    public void Child_search_scenarios_are_functional(string given, string when, string then)
    {
        var r = Classify(given, when, then);
        r.Type.Should().Be(ScenarioRequirementType.Functional, r.Explanation);
        r.Qualities.Should().BeEmpty("no performance or other quality is invented for a search scenario");
        r.Basis.Should().Be(ScenarioTypeBasis.ScenarioContent);
    }

    [Theory] // Case B — Frontend/specs/003-designer-polish/spec.md, User Story 2 «Active menu item uses correct text colour».
    [InlineData("a user is in the application", "they click a menu item", "the menu item text is displayed in colour #212121 (or the equivalent design system token)")]
    [InlineData("a user is on any page", "they look at the active element in the left sidebar", "the text colour is visually consistent with the design system's menu colours")]
    public void Menu_colour_scenarios_are_non_functional_visual_consistency(string given, string when, string then)
    {
        var r = Classify(given, when, then);
        r.Type.Should().Be(ScenarioRequirementType.NonFunctional, r.Explanation);
        r.Qualities.Should().Equal(QualityAttribute.Usability);
    }

    [Theory] // Case C — Frontend/specs/003-designer-polish/spec.md, User Story 5 «Keyboard users can skip navigation», scenarios 1–4.
    [InlineData("a user loads a page", "they press Tab for the first time", "a \"Hopp til hovedinnhold\" link is visibly displayed on screen")]
    [InlineData("the link is visible and focused", "the user presses Enter", "focus moves to the page's primary content region")]
    [InlineData("a user is navigating with a mouse (not keyboard)", "they view the page", "the skip link is not visible (visually hidden)")]
    [InlineData("a screen reader is active", "the user navigates with Tab", "the link is announced correctly by the screen reader")]
    public void Skip_navigation_scenarios_are_universal_design(string given, string when, string then)
    {
        var r = Classify(given, when, then);
        r.Type.Should().Be(ScenarioRequirementType.NonFunctional, r.Explanation);
        r.Qualities.Should().Contain(QualityAttribute.Accessibility);
        ScenarioTypeClassifier.Label(QualityAttribute.Accessibility).Should().Be("Universell utforming");
        ScenarioTypeClassifier.Label(QualityAttribute.Availability).Should().Be("Tilgjengelighet / oppetid", "the two are never the same word");
    }

    [Fact] // Case D — Proxy/specs/002-adr026-health-checks/spec.md, User Stories 1 and 2 (liveness / readiness).
    public void Health_probe_scenarios_weigh_api_behaviour_and_availability()
    {
        var liveness = Classify("the proxy process is running", "the liveness endpoint receives a request", "it returns HTTP 200 with a healthy status and performs no checks against upstream services.");
        liveness.Type.Should().Be(ScenarioRequirementType.Mixed, liveness.Explanation);
        liveness.Qualities.Should().Contain(QualityAttribute.Availability);
        var notReady = Classify("all configured backend destinations are unreachable", "the readiness endpoint receives a request", "it returns a non-200 status (e.g., 503) indicating the proxy is not ready.");
        notReady.Type.Should().Be(ScenarioRequirementType.Mixed, notReady.Explanation);
        var noAuth = Classify("the liveness endpoint is called", "the response is inspected", "no authentication is required.");
        noAuth.Type.Should().Be(ScenarioRequirementType.Functional, "an access rule is behaviour, not a quality: " + noAuth.Explanation);
    }

    [Fact] // Case E — PersonAdapter/specs/001-birk-person-adapter/spec.md, «security level 2 (Kode 6)» scenario 1 (Then shortened at (d)).
    public void Security_classified_concrete_behaviour_is_functional_not_automatically_non_functional()
    {
        var r = Classify("a CDC record with security level 2 (Kode 6)", "the adapter processes it",
            "(a) no data is forwarded to PersonModule, (b) the event is logged as a critical security incident with the BiRK ID and timestamp, (c) an immediate operational alert is triggered that requires manual acknowledgment and does not auto-resolve");
        r.Type.Should().Be(ScenarioRequirementType.Functional, r.Explanation);
    }

    [Fact] // Case F — SkoleService/specs/001-skoleservice/spec.md, scenario 2 of the Kode 6/7 story.
    public void Graded_child_business_rules_stay_functional_despite_security_terminology()
    {
        var r = Classify("stored data for a Kode 6/7 child", "a user without `Person:SeGradertBarn` for that child reads any list or lookup",
            "the child does not appear and is not counted, and a direct lookup answers as if the child did not exist.");
        r.Type.Should().Be(ScenarioRequirementType.Functional, r.Explanation);
    }

    [Fact] // Case G — a global quality criterion never makes an acceptance scenario non-functional; only links to NFRs do.
    public void Global_criteria_and_functional_requirement_links_never_force_non_functional()
    {
        var texts = new Dictionary<string, string> { ["FR-001"] = "The page must meet WCAG 2.1 AA.", ["NFR-002"] = "Search must respond within 2 seconds." };
        var functional = ScenarioTypeClassifier.Classify(Ac("x", "they search", "the matching child appears in the results", "", "FR-001"), null, texts);
        functional.Type.Should().Be(ScenarioRequirementType.Functional, "an FR link is not a type, and an FR's quality wording is not the scenario's");
        var nfr = ScenarioTypeClassifier.Classify(Ac("x", "they search", "the search completes", "", "NFR-002"), null, texts);
        nfr.Basis.Should().Be(ScenarioTypeBasis.RequirementLink);
        nfr.Qualities.Should().Contain(QualityAttribute.Performance);
    }

    [Fact] // Case H — Frontend/specs/002-child-search-profile/spec.md, User Story 2 scenario 5: a message AND a visual property.
    public void A_scenario_with_both_a_functional_outcome_and_a_quality_assertion_is_mixed()
    {
        var r = Classify("a panel fails to load its data due to a system error", "the profile loads",
            "that panel displays \"OBS! Det skjedde en teknisk feil\" in a visually distinct error style, without affecting the other panels");
        r.Type.Should().Be(ScenarioRequirementType.Mixed, r.Explanation);
        r.Qualities.Should().Contain(QualityAttribute.Usability);
    }

    [Fact] // Case I — nothing to decide from.
    public void Unclear_or_missing_outcomes_are_unclassified_never_functional_by_default()
    {
        Classify("x", "the user does it", "it works as expected").Type.Should().Be(ScenarioRequirementType.Unclassified);
        var none = ScenarioTypeClassifier.Classify(new DiscoveredScenario { CapabilityId = "specification", CanonicalKey = "scenario:m|s", Title = "t" });
        none.Type.Should().Be(ScenarioRequirementType.Unclassified);
        none.Basis.Should().Be(ScenarioTypeBasis.Unresolved);
        ScenarioTypeClassifier.BasisLabel(none.Basis).Should().Be("Trenger avklaring");
    }

    [Fact]
    public void An_explicit_marker_wins_and_a_reviewer_decision_overrides_until_the_scenario_text_changes()
    {
        ScenarioTypeClassifier.Classify(Ac("x", "they open it", "the page is shown", "Response time (NFR)")).Basis.Should().Be(ScenarioTypeBasis.Explicit);
        var ac = Ac("x", "they search", "the matching child appears in the results");
        var decision = new ScenarioTypeDecision("tss-1", null, ScenarioRequirementType.Mixed, [QualityAttribute.Performance], "Søket har en tidsgrense i planen", "anna", DateTimeOffset.UtcNow, "f1");
        var reviewed = ScenarioTypeClassifier.Classify(ac, decision);
        reviewed.Should().Match<ScenarioTypeResult>(r => r.Type == ScenarioRequirementType.Mixed && r.Reviewed && r.Qualities.Contains(QualityAttribute.Performance));
        ScenarioTypeClassifier.BasisLabel(reviewed.Basis).Should().Be("Bekreftet av gjennomgang");
        var changed = ScenarioTypeClassifier.Classify(ac with { Fingerprint = "f2" }, decision);
        changed.Reviewed.Should().BeFalse("a decision made on another version of the scenario is not applied silently");
        changed.Explanation.Should().Contain("må gjøres på nytt");
        ScenarioTypeClassifier.Classify(ac, decision with { Type = null }).Basis.Should().Be(ScenarioTypeBasis.ScenarioContent, "no type = back to the proposal");
    }

    [Fact]
    public void A_reviewed_type_is_stored_append_only_in_the_grouping_document_without_touching_test_kinds()
    {
        var doc = new StoryGroupingDocument { ProjectKey = "p", SpecificationPath = "s/spec.md" };
        var op = new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetScenarioType, ScenarioId = "tss-1", ScenarioType = ScenarioRequirementType.NonFunctional,
            Qualities = [QualityAttribute.Accessibility, QualityAttribute.Accessibility], ScenarioFingerprint = "f1", Reason = "WCAG" };
        var after = StoryGroupingEngine.Apply(doc, [op], "anna", DateTimeOffset.UtcNow, _ => true);
        var again = StoryGroupingEngine.Apply(after, [op with { ScenarioType = ScenarioRequirementType.Functional, Qualities = [QualityAttribute.Performance] }], "bob", DateTimeOffset.UtcNow, _ => true);
        again.ScenarioTypes.Should().HaveCount(2, "history is kept");
        var current = StoryGroupingEngine.ScenarioTypeDecisions(again)["tss-1"];
        current.Should().Match<ScenarioTypeDecision>(d => d.Type == ScenarioRequirementType.Functional && d.Qualities.Count == 0 && d.By == "bob", "a functional decision carries no quality");
        again.TestKinds.Should().BeEmpty();
        again.History.Should().Contain(h => h.Action == "Vurderte scenariotype");
    }
}
