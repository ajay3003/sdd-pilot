using BirkNext.Applicability;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Product availability is one catalog keyed by feature id; it outranks applicability in navigation and is never recommended.</summary>
public sealed class FeatureAvailabilityTests
{
    private static NavItem Azure => NavigationCatalog.Sections.SelectMany(s => s.Items).Single(i => i.Route == "azure-environment");

    [Fact]
    public void Azure_environment_analysis_is_unavailable_with_audit_backed_capabilities_only()
    {
        var azure = FeatureAvailabilityCatalog.Find(Azure.FeatureId)!;
        azure.State.Should().Be(FeatureAvailabilityState.Unavailable);
        azure.Capabilities.Select(c => c.Status).Should().OnlyContain(s => s == "Available" || s == "Partial" || s == "Unavailable" || s == "Not configured" || s == "Not verified");
        FeatureAvailabilityCatalog.Find("source-analysis").Should().BeNull("features absent from the catalog are available");
        FeatureAvailabilityCatalog.IsUnavailable(null).Should().BeFalse();
    }

    [Fact]
    public void Navigation_shows_unavailable_above_applicability_and_provider_overlays_still_work_elsewhere()
    {
        var applicable = new ReviewApplicability { Status = ApplicabilityStatus.Applicable, Reason = "Azure evidence was found." };
        var status = NavStatusPresentation.For(Azure, applicable)!;
        status.Kind.Should().Be(NavStatusKind.Unavailable);
        status.Spoken.Should().Be("Feature unavailable");
        NavStatusPresentation.For(Azure, null)!.Kind.Should().Be(NavStatusKind.Unavailable, "availability needs no applicability state");

        var provider = Azure with { FeatureId = null };
        NavStatusPresentation.For(provider, applicable)!.Should().Match<NavStatus>(s => s.Kind == NavStatusKind.Provider && s.Text == "Azure", "the provider overlay is unchanged for available features");
        NavStatusPresentation.For(provider, applicable with { Status = ApplicabilityStatus.NeedsConfiguration })!.Kind.Should().Be(NavStatusKind.Setup);
    }
}
