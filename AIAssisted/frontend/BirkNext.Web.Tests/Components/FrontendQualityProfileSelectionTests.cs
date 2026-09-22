using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The accessibility profile is review SCOPE: which criteria the review is judged against. It is a different concern
/// from engine activation, it is persisted with the Target Environment's WCAG settings, and the profile the user
/// picks before a run is the profile the result is reported under.
/// </summary>
public sealed class FrontendQualityProfileSelectionTests : BunitContext
{
    private IRenderedComponent<FrontendQualityProfileCard> Card(WcagAssessmentProfile? selected = null, Action<string>? onChange = null) =>
        Render<FrontendQualityProfileCard>(p => p
            .Add(c => c.Selected, selected ?? WcagProfiles.Norwegian)
            .Add(c => c.ProfileChanged, id => onChange?.Invoke(id)));

    // ── 1–2. Default profile and its derived scope ──────────────────────────

    [Fact]
    public void NorwegianPublicSectorIsTheDefaultForANewReview()
    {
        // The default lives in the persisted settings model, not in the UI.
        new WcagSettings().ProfileId.Should().Be(WcagProfiles.NorwegianId);
        new WcagSettings().Profile.IsLegalBaseline.Should().BeTrue();

        Card().Find("[data-testid=fqr-profile-name]").TextContent.Trim()
            .Should().Be("Norwegian public-sector requirements — WCAG 2.1");
    }

    [Fact]
    public void ScopeCountComesFromTheProfileRegistryNotAHardCodedNumber()
    {
        // 48 is asserted through the registry, so a change to the criteria list moves the UI with it.
        WcagProfiles.Norwegian.CriteriaInScope.Should().Be(48);
        WcagProfiles.Norwegian.CriterionIds.Should().HaveCount(48).And.OnlyHaveUniqueItems();

        // 4, 8, 9. The count is the SIZE OF THE PROFILE, which is all that is known before a review runs.
        // "Applicable" is a property of the target — which criteria a page actually engages — and nothing has been
        // assessed at this point, so the word claimed a determination that had not been made.
        Card().Find("[data-testid=fqr-profile-scope]").TextContent.Trim()
            .Should().Be($"{WcagProfiles.Norwegian.CriteriaInScope} criteria in selected profile");
        Card().Markup.Should().NotContain("applicable");
    }

    // ── 3–5. All three profiles are offered and correctly labelled ──────────

    [Fact]
    public void ChangeOffersTheThreeProfilesWithTheLegalBaselineFirst()
    {
        var cut = Card();
        cut.Find("[data-testid=fqr-profile-change]").Click();

        cut.FindAll("[data-testid=fqr-profile-options] .fqr-profile-option-name").Select(n => n.TextContent.Trim())
            .Should().Equal(
                "Norwegian public-sector requirements — WCAG 2.1",
                "WCAG 2.1 AA — Full standard",
                "WCAG 2.2 AA — Extended review");

        // Only the statutory subset is marked as a legal baseline.
        cut.FindAll("[data-testid=fqr-profile-legal]").Should().ContainSingle();
    }

    [Fact]
    public void Wcag22IsAnExtendedReviewAndNeverLabelledAsNorwegianLegalRequirement()
    {
        WcagProfiles.Extended.Label.Should().Be("WCAG 2.2 AA — Extended review");
        WcagProfiles.Extended.IsLegalBaseline.Should().BeFalse();
        WcagProfiles.Extended.Description.Should().Contain("not the Norwegian legal baseline");

        var cut = Card(WcagProfiles.Extended);
        cut.Find("[data-testid=fqr-profile-name]").TextContent.Should().NotContainAny("Norwegian", "legal requirement");
    }

    [Fact]
    public void FullStandardIsBroaderThanTheStatutorySubsetAndIsNotALegalBaseline()
    {
        var full = WcagProfiles.Wcag21Aa;

        full.Label.Should().Be("WCAG 2.1 AA — Full standard");
        full.IsLegalBaseline.Should().BeFalse();
        full.WcagVersion.Should().Be(WcagVersion.Wcag21);
        full.CriteriaInScope.Should().BeGreaterThan(WcagProfiles.Norwegian.CriteriaInScope);
        // It is a 2.1 profile, so it must not pull in criteria that only exist in 2.2.
        full.CriterionIds.Should().OnlyContain(id => WcagRegistry.All.Single(d => d.CriterionId == id).Since == WcagVersion.Wcag21);
    }

    // ── 6–7. Selection is raised for the host to persist ────────────────────

    [Fact]
    public void ChoosingAProfileRaisesTheChangeForTheHostToPersist()
    {
        string? chosen = null;
        var cut = Card(onChange: id => chosen = id);
        cut.Find("[data-testid=fqr-profile-change]").Click();

        cut.Find($"[data-testid=fqr-profile-option-{WcagProfiles.ExtendedId}]").Change(true);

        chosen.Should().Be(WcagProfiles.ExtendedId);
        // The card collapses back to its compact form; it never becomes a permanent management form.
        cut.FindAll("[data-testid=fqr-profile-options]").Should().BeEmpty();
    }

    [Fact]
    public void AnExplicitlySavedProfileIsShownInsteadOfTheDefault()
    {
        var saved = new WcagSettings { ProfileId = WcagProfiles.ExtendedId };

        saved.Profile.ProfileId.Should().Be(WcagProfiles.ExtendedId);
        Card(saved.Profile).Find("[data-testid=fqr-profile-name]").TextContent.Trim()
            .Should().Be("WCAG 2.2 AA — Extended review");
    }

    // ── 8–9. Selected profile drives the assessment scope ───────────────────

    [Theory]
    [InlineData(WcagProfiles.NorwegianId)]
    [InlineData(WcagProfiles.Wcag21AaId)]
    [InlineData(WcagProfiles.ExtendedId)]
    public void TheSelectedProfileIsTheProfileTheResultIsReportedUnder(string profileId)
    {
        var assessment = WcagAssessmentEngine.Evaluate(new EndpointDiscoverySnapshot
        {
            Wcag = new WcagSettings { ProfileId = profileId },
            Pages = [new() { PagePath = "/" }],
        });

        assessment.Profile!.ProfileId.Should().Be(profileId);
        assessment.TargetLabel.Should().Be(WcagProfiles.Resolve(profileId).Label);
        // Criteria in the result match the profile's scope exactly — no drift between selector and result.
        assessment.Results.Select(r => r.Definition.CriterionId).Distinct()
            .Should().BeEquivalentTo(WcagProfiles.Resolve(profileId).CriterionIds);
    }

    // ── 10. No stale wording ────────────────────────────────────────────────

    [Fact]
    public void ObsoleteProfileWordingIsGone()
    {
        foreach (var profile in WcagProfiles.Available)
        {
            profile.Label.Should().NotContain("Legacy WCAG assessment");
            profile.Label.Should().NotContain("WCAG compliant");
        }

        // A genuinely unknown saved profile still says so rather than claiming a current one.
        WcagProfiles.Resolve("legacy-unknown").Label.Should().Be("Saved assessment — Profile unknown");
        WcagProfiles.Resolve("legacy-unknown").IsLegalBaseline.Should().BeFalse();
    }

    // ── Profile is scope, not engine configuration ──────────────────────────

    [Fact]
    public void TheProfileCardOffersNoEngineConfiguration()
    {
        var cut = Card();
        cut.Find("[data-testid=fqr-profile-change]").Click();

        // Every control is a profile radio; nothing here enables or disables an engine.
        cut.FindAll("input").Should().OnlyContain(i => i.GetAttribute("type") == "radio");
        cut.FindAll("input").Should().OnlyContain(i => i.GetAttribute("name") == "fqr-profile");
        cut.Markup.Should().NotContainAny("Accessibility engine", "Browser Quality", "Lighthouse", "Enabled", "Unavailable");
    }

    // ── Accessibility of the selector itself ────────────────────────────────

    [Fact]
    public void SelectorIsAKeyboardOperableSingleChoiceWithTheSelectionExposed()
    {
        var cut = Card(WcagProfiles.Extended);
        cut.Find("[data-testid=fqr-profile-change]").Click();

        var options = cut.FindAll("[data-testid=fqr-profile-options] input").ToList();
        options.Should().HaveCount(3);
        options.Count(o => o.HasAttribute("checked")).Should().Be(1, "exactly one profile is selected");
        cut.Find($"[data-testid=fqr-profile-option-{WcagProfiles.ExtendedId}]").HasAttribute("checked").Should().BeTrue();

        cut.Find("[data-testid=fqr-profile-options]").TagName.Should().Be("FIELDSET");
        cut.Find(".fqr-profile-legend").TextContent.Trim().Should().Be("Evaluate accessibility against");
        cut.Find("#fqr-profile-heading").TagName.Should().Be("H2");
    }
}
