using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

public sealed class SkoleNaervaerTestingNavigationTests
{
    [Fact]
    public void Skolenarvaer_IsRegisteredOnceUnderExtensions_AlongsideSecurityClassification()
    {
        var rows = NavigationCatalog.Sections.SelectMany(section => section.Items.Select(item => (section.Id, Item: item))).ToList();

        rows.Where(row => row.Item.Label is "M01 – Gradert tilgang (Kode 6/7)" or "Skolenærvær Testing")
            .Select(row => (row.Id, row.Item.Label, row.Item.Route))
            .Should().ContainInOrder(
                ("extensions", "M01 – Gradert tilgang (Kode 6/7)", "security-classification-review"),
                ("extensions", "Skolenærvær Testing", "extensions/skolenaervaer"));
        rows.Count(row => row.Item.Route == "extensions/skolenaervaer").Should().Be(1);
        rows.Where(row => row.Item.Route == "extensions/skolenaervaer").Single().Id.Should().Be("extensions");
        rows.Where(row => row.Id == "quality-testing").Select(row => row.Item.Label)
            .Should().NotContain("Skolenærvær Testing");
    }

    [Fact]
    public void Skolenarvaer_DocumentationUsesStableRouteAndSharedEngineBoundary()
    {
        var feature = DocumentationCatalog.Features.Single(item => item.FeatureId == "extensions/skolenaervaer");
        var detail = DocumentationCatalog.DetailFor(feature.FeatureId);

        feature.Route.Should().Be("/extensions/skolenaervaer");
        detail.Boundary.Should().Contain("shared services");
    }
}
