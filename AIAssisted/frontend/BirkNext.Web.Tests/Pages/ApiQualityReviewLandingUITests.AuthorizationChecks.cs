using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Task 9: readiness-first pre-run (§53–§55, §65) and the authorization-check deep link (§62–§64).</summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    // §53, §71. Current fixture: REST without OpenAPI + GraphQL with runtime introspection, authenticated context available.
    [Fact]
    public void PreRun_LeadsWithTheRunDecision_CompactScope_TheMainLimitation_AndOnePrimaryAction()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));

        page.Find("#aqr-readiness-heading").TextContent.Trim().Should().EndWith("Ready to run with limitations");
        page.Find("[data-testid=aqr-decision-counts]").TextContent.Should().Be("2 API targets selected · 2/2 access ready");
        page.Find("[data-testid=aqr-main-limitation]").TextContent.Should().Contain("REST contract validation unavailable for Autorisasjon API.")
            .And.Contain("structural response, security, error-handling and performance checks");
        page.Find("[data-testid=aqr-decision-graphql]").TextContent.Should().Contain("Will be inspected at runtime");
        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("2 selected");
        page.Find("[data-testid=aqr-scope-protocols]").TextContent.Should().Be("1 REST · 1 GraphQL");
        page.Find("[data-testid=aqr-target-access-summary]").TextContent.Should().Be("2/2 ready");

        // §48: one strong primary action on the whole pre-run, and it is Run.
        page.FindAll("[data-testid=aqr-decide] .btn-primary").Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Equal("Run API Quality Review");
        // §43: diagnostics collapsed.
        page.Find("[data-testid=aqr-diagnostics-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    // §54. Review state per target: REST without a contract is Limited, GraphQL with runtime introspection is Ready, both explained plainly.
    [Fact]
    public void PreRun_EachTargetStatesItsReviewState_AndWhatCanAndCannotRun()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-target-review]").Should().HaveCount(2));

        var rest = page.Find("[data-testid=aqr-target-review][data-type=REST]");
        rest.QuerySelector("[data-testid=aqr-target-review-state]")!.TextContent.Should().Be("Limited");
        rest.QuerySelector("[data-testid=aqr-target-review-contract]")!.TextContent.Should().Be("Not configured");
        rest.QuerySelector("[data-testid=aqr-target-review-not-runs]")!.TextContent.Should().Contain("Contract compatibility validation");
        rest.QuerySelector("[data-testid=aqr-target-configure-contract]")!.GetAttribute("href").Should().Contain("tab=target").And.Contain("profile=dev");

        var gql = page.Find("[data-testid=aqr-target-review][data-type=GraphQL]");
        gql.QuerySelector("[data-testid=aqr-target-review-state]")!.TextContent.Should().Be("Ready");
        gql.QuerySelector("[data-testid=aqr-target-review-contract]")!.TextContent.Should().Be("Runtime introspection");
        gql.QuerySelector("[data-testid=aqr-target-review-note]")!.TextContent.Should().Be("The GraphQL schema will be retrieved through introspection during the review.");
        gql.QuerySelector("[data-testid=aqr-target-review-fallback]")!.TextContent.Should().Be("Not configured");
        gql.QuerySelector("[data-testid=aqr-target-configure-fallback]")!.TextContent.Should().Be("Configure fallback");
    }

    // §55, §41, §65. Optional active checks are compact and never block the read-only review.
    [Fact]
    public void PreRun_OptionalActiveChecksAreCompact_AndMissingAuthorizationChecksDoNotBlock()
    {
        Execution(TrustedTargetState.NotRegistered);
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization-summary]"));

        page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().NotBe("Blocked");
        page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse("authorization checks are optional");
        page.Find("[data-testid=aqr-fuzzing-state]").TextContent.Should().Be("Off");
        page.Find("[data-testid=aqr-authorization-summary]").TextContent.Should().StartWith("2 available · 0 ready").And.Contain("blocked");
        page.Find("[data-testid=aqr-authorization-impact]").TextContent.Should().Be("Not run · does not block read-only API review.");
        page.Find("[data-testid=aqr-active-checks-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-additional-checks]").TextContent.Should().NotContain("Authorization scenarios", "tester-facing wording is Authorization checks");
    }

    // §62, §63. The link lands on the editor (focus + return carry the environment and the selection); browser Back restores the scope.
    [Fact]
    public void ReviewAuthorizationChecks_DeepLinksToTheEditor_AndTheSelectionSurvivesTheRoundTrip()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-target-review]").Should().HaveCount(2));

        // Deselect GraphQL: the URL now carries the narrowed scope.
        page.Find("[data-testid=aqr-targets-disclosure-toggle]").Click();
        page.FindAll("[data-testid=aqr-target-checkbox]")[1].Change(false);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.Uri.Should().EndWith($"/api-quality-review?targets={Uri.EscapeDataString(RestId)}");

        var link = page.Find("[data-testid=aqr-configure-authorization]");
        link.TextContent.Should().Be("Review authorization checks");
        var href = link.GetAttribute("href")!;
        href.Should().StartWith("/admin/system-settings?section=target-environments&tab=endpoint&profile=dev&focus=authorization-checks&return=");
        Uri.UnescapeDataString(href[(href.IndexOf("return=", StringComparison.Ordinal) + 7)..])
            .Should().Be($"/api-quality-review?targets={Uri.EscapeDataString(RestId)}");
        FrontendAnalysisSettingsReturn.IsAllowed(Uri.UnescapeDataString(href[(href.IndexOf("return=", StringComparison.Ordinal) + 7)..])).Should().BeTrue();
    }

    [Fact]
    public void ReturningWithASelectionInTheUrl_RestoresExactlyThatScope()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/api-quality-review?targets={Uri.EscapeDataString(RestId)}");
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-target-review]").Should().ContainSingle());
        page.Find("[data-testid=aqr-target-review]").GetAttribute("data-target-id").Should().Be(RestId);
        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("1 selected");
    }

    // §64. Save/return refreshes immediately: the counts come from the saved Target Environment on every render.
    [Fact]
    public void ConfiguredAndMappedChecks_AreCountedFromTheSavedTargetEnvironment()
    {
        Execution(TrustedTargetState.Trusted);
        var context = WithRuntimeSecurity(Context(), r =>
        {
            r.AuthorizationScenarios.Add(UiScenario());
            r.AuthorizationScenarios.Add(new AuthorizationScenario
            {
                ScenarioId = "admin", Url = $"https://{ApiHost}/api/autorisasjon/brukere", Origin = AuthorizationCheckOrigin.SourceDerived,
                Confirmation = AuthorizationCheckConfirmation.Confirmed,
                Identities = [new() { Alias = "", Role = "Admin", Expected = AuthorizationExpectation.Allow }],
            });
        });
        Register(context, true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization-summary]").TextContent.Should().Contain("1 needs identity mapping"));
        page.Find("[data-testid=aqr-authorization-impact]").TextContent.Should().Contain("does not block read-only API review");
    }
}
