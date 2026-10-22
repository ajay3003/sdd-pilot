using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>The shared Security Classification projection: one consumer's requirements (a test pack) or every consumer (the standalone
/// review), the same shared rows for each, domain rules marked as the consumer's own, read-only with a link to the full review.</summary>
public sealed class SecurityConsumerProjectionTests : BunitContext
{
    private static readonly Guid RunId = Guid.Parse("5ec00000-0000-0000-0000-000000000020");

    private static SecurityClassificationConsumerAssessment Consumer(string id, string name, params SecurityRequirementAssessment[] extra) => new(id, name, $"{name} context", RunId,
        DateTimeOffset.Parse("2026-10-22T08:00:00Z"),
        [new("unauthorized-access", "Unauthorized access denied", SecurityRequirementState.Verified, "Source: SourceEvidence", "1 observation(s) on DirectProfile (level 3) — proves these surfaces only, never all authorization.", "Security Classification review"), .. extra],
        "1 of 2 requirement(s) not verified yet.");

    private Mock<IClassificationReviewApiService> Api(params SecurityClassificationConsumerAssessment[] consumers)
    {
        var api = new Mock<IClassificationReviewApiService>();
        api.Setup(a => a.ConsumersAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(consumers);
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public void A_consumer_projection_shows_its_requirements_marks_domain_rules_and_links_to_the_full_review()
    {
        var api = Api(Consumer("m01", "M01 – Gradert tilgang (Kode 6/7)"),
            Consumer("skolenaervaer", "Skolenærvær", new SecurityRequirementAssessment("bufdata-exclusion", "Bufdata exclusion rule", SecurityRequirementState.NotTested,
                "The Bufdata dataset must not contain fødselsnummer or DUF.", "No runtime evidence for this domain rule yet.", "Skolenærvær Testing") { DomainSpecific = true }));
        var cut = Render<SecurityConsumerProjection>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.ConsumerId, "skolenaervaer"));

        cut.FindAll("[data-testid=sc-consumer]").Should().ContainSingle().Which.GetAttribute("data-consumer").Should().Be("skolenaervaer");
        var rows = cut.FindAll("[data-testid=sc-consumer-requirement]");
        rows.Select(r => r.GetAttribute("data-key")).Should().Equal("unauthorized-access", "bufdata-exclusion");
        rows[0].TextContent.Should().Contain("proves these surfaces only");
        rows[1].TextContent.Should().Contain("Skolenærvær rule · owner: Skolenærvær Testing");
        cut.Find("[data-testid=sc-open-full-review]").GetAttribute("href").Should().Be("/security-classification-review");
        cut.FindAll("input, select, textarea").Should().BeEmpty("no second source selection or test context");
        api.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<ClassificationRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_standalone_review_lists_every_consumer_of_the_same_evidence()
    {
        Api(Consumer("m01", "M01 – Gradert tilgang (Kode 6/7)"), Consumer("skolenaervaer", "Skolenærvær"));
        var cut = Render<SecurityConsumerProjection>(p => p.Add(x => x.EnvironmentId, "dev"));

        cut.FindAll("[data-testid=sc-consumer]").Select(c => c.GetAttribute("data-consumer")).Should().Equal("m01", "skolenaervaer");
        cut.FindAll("[data-testid=sc-consumer-summary]").Should().OnlyContain(s => s.TextContent.Contains("From review 5ec00000"), "every consumer references the same run");
        cut.FindAll("[data-testid=sc-open-full-review]").Should().BeEmpty("the standalone review is the full review");
    }
}
