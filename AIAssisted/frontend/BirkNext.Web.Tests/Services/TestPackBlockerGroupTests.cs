using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The generic blocker-group model: low-level prerequisite checks become a few owner-aware tester actions. A value already known from
/// guidance but not saved is "Known · not saved for active run" (applied only explicitly), active execution is never "trusted" wording, an
/// unbound source is never Missing, and every underlying check stays in its group. Generic: no pack is named.
/// </summary>
public sealed class TestPackBlockerGroupTests
{
    private static JourneyPrerequisite P(string key, JourneyPrerequisiteCategory category, JourneyReadinessState state = JourneyReadinessState.NotReady) => new(key, key, category, state, "detail");

    private static TestPackSourceGuidance Known(params string[] prerequisiteKeys) => new("demo",
    [
        .. prerequisiteKeys.Select(key => new SourceConfigurationSuggestion(new SourceConfigurationRequirement(key, $"Value {key}", "Application.Route", "App", "Reference", "p", "Owner")
            { PrerequisiteKey = key, AutoApply = true }, SourceSuggestionState.Reference) { ResolvedValue = $"known-{key}", ResolvedFrom = ConfigurationValueOrigin.TestPackReference }),
    ]);

    [Fact]
    public void FifteenChecks_BecomeAtMostSixActions_EachWithOwnerAndAction_AndNoCheckIsLost()
    {
        var checks = new[]
        {
            P("gate", JourneyPrerequisiteCategory.TrustedEnvironment), P("app-env", JourneyPrerequisiteCategory.TrustedEnvironment), P("app-id", JourneyPrerequisiteCategory.Integration),
            P("client", JourneyPrerequisiteCategory.Authentication), P("redirect", JourneyPrerequisiteCategory.Authentication), P("secret", JourneyPrerequisiteCategory.Authentication),
            P("provider", JourneyPrerequisiteCategory.Authentication), P("policy", JourneyPrerequisiteCategory.Authorization), P("reporter", JourneyPrerequisiteCategory.SyntheticData),
            P("profile", JourneyPrerequisiteCategory.SyntheticData), P("child", JourneyPrerequisiteCategory.SyntheticData), P("contract", JourneyPrerequisiteCategory.Contract),
            P("metadata", JourneyPrerequisiteCategory.Contract), P("process", JourneyPrerequisiteCategory.Contract), P("xsd", JourneyPrerequisiteCategory.Contract),
        };
        var groups = TestPackPresentation.BlockerGroups(checks, Known("app-env", "app-id"), "dev");

        groups.Select(g => g.Key).Should().Equal("active-execution", "run-configuration", "authentication", "authorization", "test-data", "contract");
        groups.Should().HaveCountLessThanOrEqualTo(6);
        groups.Sum(g => g.Items.Count).Should().Be(15, "the underlying checks stay available in their group");
        groups.Should().OnlyContain(g => g.Owner.Length > 0 && g.ActionLabel.Length > 0 && (g.ActionHref != null || g.ActionGroup != null));
        var execution = groups[0];
        (execution.Title, execution.State, execution.ActionLabel, execution.Owner).Should().Be(("Active execution", "Not allowed", "Manage active execution", "Target Environment"));
        string.Join(" ", groups.Select(g => $"{g.Title} {g.State} {g.Explanation}")).Should().NotContainAny("Trusted", "trusted", "enrolled");
        var known = groups[1];
        (known.State, known.ActionHref).Should().Be(("Known · not saved for active run", TestPackPresentation.SafeConfigurationAnchor), "a known value is never called missing");
        known.Items.Select(i => i.Key).Should().Equal("app-env", "app-id");
        known.KnownValues.Should().Contain(v => v.Contains("known-app-env") && v.Contains("Test Pack reference"));
    }

    [Fact]
    public void UnboundSource_IsAvailableNeedsBinding_AndMissingSourceAsksForAnImport()
    {
        var available = TestPackPresentation.BlockerGroups([P("source:hub", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.Partial)], null, "dev").Single();
        (available.State, available.ActionLabel).Should().Be(("Available · needs binding", "Bind source evidence"));
        var missing = TestPackPresentation.BlockerGroups([P("source:hub", JourneyPrerequisiteCategory.SourceEvidence)], null, "dev").Single();
        (missing.State, missing.ActionLabel, missing.ActionHref).Should().Be(("Missing", "Import source", "project-import?import=source"));
    }

    [Fact]
    public void ASourceThatOnlyNeedsChoosingOrBinding_IsNeverMissing_EvenWhenTheComponentIsNotFoundYet()
    {
        var role = new TestPackSourceRole("downstream", "Downstream", "") { Journeys = ["intake"] };
        var check = P("source:Adapter", JourneyPrerequisiteCategory.SourceEvidence);
        var ambiguous = TestPackPresentation.BlockerGroups([check], null, "dev", journeySources: [new(role, TestPackSourceState.ReviewRequired)]).Single();
        (ambiguous.Title, ambiguous.State, ambiguous.ActionLabel).Should().Be(("Downstream source evidence", "Review required", "Choose Downstream evidence"));
        var unbound = TestPackPresentation.BlockerGroups([check], null, "dev", journeySources: [new(role, TestPackSourceState.AvailableNotBound)]).Single();
        (unbound.State, unbound.ActionLabel).Should().Be(("Available · needs binding", "Bind Downstream evidence"));
    }

    [Fact]
    public void AnIntegrationOwnedValue_IsNotListedAsKnownButUnsaved_BecauseTheRunReadsItFromTheIntegration()
    {
        var guidance = new TestPackSourceGuidance("demo",
        [
            new(new SourceConfigurationRequirement("hook", "Webhook", "Endpoint.EventWebhook", "App", "Integration", "p", "Owner") { PrerequisiteKey = "hook" }, SourceSuggestionState.Suggested)
                { ResolvedValue = "https://hub.example.test/hook", ResolvedFrom = ConfigurationValueOrigin.ConfiguredIntegration },
        ]);
        TestPackPresentation.BlockerGroups([P("hook", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.NotConfigured)], guidance, "dev").Single().Key.Should().Be("integration");
    }

    [Fact]
    public void ThePrimaryAction_IsWhatTheTesterResolvesFirst_NotTheEnvironmentGate()
    {
        var groups = TestPackPresentation.BlockerGroups([P("gate", JourneyPrerequisiteCategory.TrustedEnvironment),
            P("integration:x", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.NotConfigured)], null, "dev");
        groups[0].Key.Should().Be("active-execution", "the gate is still listed first");
        TestPackPresentation.PrimaryGroup(groups)!.Key.Should().Be("integration");
        TestPackPresentation.PrimaryGroup([groups[0]])!.Key.Should().Be("active-execution", "when it is the only blocker it is the next step");
    }

    [Fact]
    public void MissingIntegration_IsSaidDirectly_AndAPackMayNameAGroupInItsOwnTerms()
    {
        var groups = TestPackPresentation.BlockerGroups([P("integration:intake", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.NotConfigured),
            P("secret", JourneyPrerequisiteCategory.Authentication)], null, "dev", new Dictionary<string, string> { ["authentication"] = "Portal sign-in setup" });
        (groups[0].State, groups[0].ActionLabel).Should().Be(("Missing integration", "Configure integration"));
        groups[1].Title.Should().Be("Portal sign-in setup");
        TestPackPresentation.BlockerGroups([P("integration:intake", JourneyPrerequisiteCategory.Integration)], null, "dev").Single().Key
            .Should().Be("active-execution", "a configured integration not enabled for active runs is the execution-permission fix, not a missing integration");
    }
}
