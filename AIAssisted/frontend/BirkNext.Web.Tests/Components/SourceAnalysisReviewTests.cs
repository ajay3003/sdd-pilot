using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using BirkNext.Web.Shared.Components.Common;
using Bunit;
using FluentAssertions;
using ConfigurationEntry = BirkNext.SourceDomains.ConfigurationEntry;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis review hierarchy: legacy routes map onto the four areas, one Export menu, findings kept apart from analyzer limitations,
/// every non-complete status carries its reason from the data, zero-count metrics read "Not detected" instead of a bare 0, and quality-evidence
/// domains are summary-first. Generic fixture (<see cref="SourceEvidenceDomainsUiTests.Evidence"/>) — no project-specific names.
/// </summary>
public sealed class SourceAnalysisReviewTests : BunitContext
{
    public SourceAnalysisReviewTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static IqrSourceSnapshot Snapshot(SourceEvidenceDomainsSnapshot evidence) => new()
    {
        Id = evidence.SourceSnapshotId, IntegrationId = "source-analysis", Archive = new SourceArchive("shop.zip", evidence.SourceFingerprint, 40), AnalyzedAt = DateTimeOffset.UnixEpoch,
        Status = SourceAnalysisStatus.Ready, EvidenceDomains = evidence,
    };

    [Theory]
    [InlineData(null, null, "Overview", null)]
    [InlineData("Overview", null, "Overview", null)]
    [InlineData("architecture", null, "Architecture", null)]
    [InlineData("Database", null, "Data", null)]
    [InlineData("Data", null, "Data", null)]
    [InlineData("Observability", null, "Quality Evidence", "Observability")]
    [InlineData("Infrastructure", null, "Quality Evidence", "Infrastructure")]
    [InlineData("Configuration", null, "Quality Evidence", "Configuration")]
    [InlineData("CI/CD", null, "Quality Evidence", "CI/CD")]
    [InlineData("cicd", null, "Quality Evidence", "CI/CD")]
    [InlineData("Contracts", null, "Quality Evidence", "Contracts")]
    [InlineData("quality-evidence", null, "Quality Evidence", null)]
    [InlineData("quality-evidence", "cicd", "Quality Evidence", "CI/CD")]
    [InlineData("quality-evidence", "unknown", "Quality Evidence", null)]
    [InlineData("something-else", null, "Overview", null)]
    public void Legacy_and_current_area_links_resolve_onto_the_four_areas(string? area, string? view, string expectedArea, string? expectedDomain)
    {
        SourceAnalysisAreas.Resolve(area, view).Should().Be((expectedArea, expectedDomain));
        SourceAnalysisAreas.Top.Should().Equal("Overview", "Architecture", "Data", "Quality Evidence");
    }

    [Fact]
    public void Area_and_domain_url_values_round_trip()
    {
        foreach (var area in SourceAnalysisAreas.Top) SourceAnalysisAreas.Resolve(SourceAnalysisAreas.AreaParam(area), null).Area.Should().Be(area);
        foreach (var domain in SourceAnalysisAreas.QualityDomains)
            SourceAnalysisAreas.Resolve(SourceAnalysisAreas.AreaParam(SourceAnalysisAreas.Quality), SourceAnalysisAreas.Slug(domain)).Should().Be((SourceAnalysisAreas.Quality, domain));
    }

    [Fact]
    public void Export_menu_is_one_button_with_a_short_list_that_closes_after_a_choice_or_escape()
    {
        var ran = new List<string>();
        var cut = Render<ExportMenu>(p => p.Add(m => m.TestId, "x").Add(m => m.Options, [new("html", "HTML", () => { ran.Add("html"); return Task.CompletedTask; }), new("json", "JSON", () => { ran.Add("json"); return Task.CompletedTask; })]));
        var toggle = cut.Find("[data-testid=x-toggle]");
        toggle.TextContent.Should().Contain("Export");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("ul").HasAttribute("hidden").Should().BeTrue();
        toggle.GetAttribute("aria-controls").Should().Be(cut.Find("ul").Id);
        toggle.Click();
        cut.Find("[data-testid=x-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("ul button").Select(b => b.TextContent).Should().Equal("HTML", "JSON");
        cut.Find("[data-testid=x-json]").Click();
        ran.Should().Equal("json");
        cut.Find("ul").HasAttribute("hidden").Should().BeTrue("choosing a format closes the menu");
        cut.Find("[data-testid=x-toggle]").Click();
        cut.Find("[data-testid=x]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.Find("ul").HasAttribute("hidden").Should().BeTrue("Escape closes the menu");
    }

    [Fact]
    public void Attention_counts_findings_only_and_analyzer_limitations_are_counted_separately()
    {
        var s = Snapshot(SourceEvidenceDomainsUiTests.Evidence());
        var attention = SourceAnalysisReview.Attention(s);
        attention.Select(a => (a.Key, a.Count)).Should().Equal(("configuration-conflicts", 1), ("contracts-review", 2));
        attention.Should().OnlyContain(a => a.Area == SourceAnalysisAreas.Quality && a.Domain != null, "each item opens the matching domain");
        attention.Select(a => a.Text).Should().NotContain(t => t.Contains(SourceDomainText.SourceBoundary), "a limitation is never a finding");
        SourceAnalysisReview.Limitations(s).Should().BeGreaterThan(0, "the analyzer limitations stay reachable, but separately");
    }

    [Fact]
    public void Every_non_complete_quality_row_carries_its_reason_from_the_data()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var withUnknown = evidence with
        {
            Configuration = evidence.Configuration with
            {
                Status = SourceDomainStatus.Partial,
                Entries = [.. evidence.Configuration.Entries, new ConfigurationEntry { Id = "e5", Key = "Feature:Mode", NormalizedKey = "feature:mode", Category = ConfigurationCategory.Unknown, File = "src/Api/appsettings.json", Line = 9 }],
            },
        };
        var rows = SourceAnalysisReview.QualityRows(Snapshot(withUnknown)).ToDictionary(r => r.Key);
        rows["configuration"].Reason.Should().Be("1 uncategorized key");
        rows["contracts"].Reason.Should().Be("2 contracts parsed heuristically");
        rows["contracts"].Detail.Should().Be("2 contracts");
        rows.Values.Where(r => r.Status != "Complete").Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Reason));
        SourceAnalysisReview.Attention(Snapshot(withUnknown)).Should().Contain(a => a.Key == "configuration-uncategorized" && a.Count == 1);

        var notDetected = SourceAnalysisReview.Domain(Snapshot(SourceEvidenceDomainsUiTests.Evidence(infrastructure: false)), "Infrastructure");
        (notDetected.Status, notDetected.Reason, notDetected.Detail).Should().Be(("Not detected", SourceAnalysisReview.NotDetectedText, null), "not detected shows no zero count");
    }

    [Fact]
    public void Configuration_summary_states_classification_labels_and_redacted_sensitive_references()
    {
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, SourceEvidenceDomainsUiTests.Evidence()));
        cut.Find("[data-testid=sd-config-lead]").TextContent.Should().Be("4 entries across 2 files");
        cut.Find("[data-testid=sd-config-classification] dd").TextContent.Should().Be("4 classified · 0 uncategorized");
        cut.Find("[data-testid=sd-config-sensitive]").TextContent.Should().Be("1");
        cut.Find("[data-testid=sd-config-overview]").TextContent.Should().Contain("Values remain redacted");
        cut.FindAll("[data-testid=sd-config-review-unknown]").Should().BeEmpty("no review action for an empty set");
        cut.Find("[data-testid=sd-config-review-sensitive]").TextContent.Should().Be("Review references");
        cut.Find("[data-testid=sd-config-labels]").TextContent.Should().Contain("not verified Target Environments");
        cut.FindAll(".btn-primary").Should().ContainSingle().Which.TextContent.Should().Be("Review configuration");
    }

    [Fact]
    public void Pipeline_summary_shows_not_detected_instead_of_zero_and_only_relevant_metrics()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var cut = Render<PipelineWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.Find("[data-testid=sd-cicd-lead]").TextContent.Should().Be("1 pipeline detected", "a template is not a pipeline");
        cut.Find("[data-testid=sd-cicd-checks]").TextContent.Should().Be("1 step");
        cut.Find("[data-testid=sd-cicd-overview]").TextContent.Should().Contain("Templates").And.NotContain("IaC steps", "zero-count optional metrics are hidden");

        var noChecks = evidence with { CiCd = evidence.CiCd with { Pipelines = [.. evidence.CiCd.Pipelines.Select(p => p with { Steps = [.. p.Steps.Where(s => s.Kind != PipelineStepKind.DependencyScan)] })] } };
        cut = Render<PipelineWorkspace>(p => p.Add(x => x.Evidence, noChecks));
        cut.Find("[data-testid=sd-cicd-checks]").TextContent.Should().Be("Not detected", "a missing check is not detected in source, not a zero score");
    }

    [Fact]
    public void Infrastructure_summary_groups_declared_resources_by_kind()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, SourceEvidenceDomainsUiTests.Evidence()));
        cut.Find("[data-testid=sd-infra-lead]").TextContent.Should().Be("Infrastructure detected");
        cut.FindAll("[data-testid=sd-infra-overview] dt").Select(d => d.TextContent).Should().Equal("Resources", "Resource types", "Bindings", "Environment references", "Needs review");
        cut.FindAll("[data-testid=sd-infra-category]").Select(c => c.GetAttribute("data-category")).Should().Equal("Compute", "Messaging", "Networking");
        cut.Find("[data-testid=sd-infra-categories]").TextContent.Should().Contain("A declaration does not prove the resource exists");
        cut.Find("[data-testid=sd-infra-review]").TextContent.Should().Be("Review infrastructure");
    }

    [Fact]
    public void Contracts_show_who_uses_them_from_source_evidence_only()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var withConsumer = evidence with { Contracts = evidence.Contracts with { Contracts = [evidence.Contracts.Contracts[0] with { ConsumerHints = ["Worker"] }, evidence.Contracts.Contracts[1]] } };
        var usage = SourceAnalysisReview.Usage(withConsumer.Contracts.Contracts[0], withConsumer.CrossDomain);
        usage.ProducedBy.Should().Equal("Api");
        usage.UsedBy.Should().Equal("Worker");
        SourceAnalysisReview.Usage(withConsumer.Contracts.Contracts[1], withConsumer.CrossDomain).UsedBy.Should().BeEmpty("nothing is guessed");

        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, withConsumer));
        cut.Find("[data-testid=sd-contracts-lead]").TextContent.Should().Be("2 contracts detected");
        cut.Find("[data-testid=sd-contracts-parsing]").TextContent.Should().Be("Partial 2");
        cut.FindAll("[data-testid=sd-contracts-review-reason]").Select(r => r.TextContent).Should().Equal("Parsed heuristically: Shop API, OrderPlaced");
        cut.Find("[data-testid=sd-contracts-filter-type]").Change("OpenApi");
        cut.Find("[data-testid=sd-contract-usage]").TextContent.Should().Be("Worker");
        cut.Find("[data-testid=sd-contract-parser]").TextContent.Should().Be("Partial", "plain-language parsing, not a coloured badge only");
        cut.Find("[data-testid=sd-contracts-filter-type]").Change("MessageContract");
        cut.Find("[data-testid=sd-contract-usage]").TextContent.Should().Be("Not stated in source");
    }

    [Fact]
    public void CiCd_reason_comes_from_the_pipelines_before_falling_back_to_the_static_reading_limit()
    {
        var p = SourceEvidenceDomainsUiTests.Evidence().CiCd;
        SourceAnalysisReview.CiCdReason(p).Should().Be("Templates, expressions and conditions are not evaluated");
        var manual = p with { Pipelines = [.. p.Pipelines.Select(x => x with { Triggers = [new PipelineTrigger { Type = "manual" }] })] };
        SourceAnalysisReview.CiCdReason(manual).Should().Be("1 pipeline has no CI trigger", "a template is not counted");
        var other = p with { Pipelines = [.. p.Pipelines.Select(x => x with { Steps = [.. x.Steps, new PipelineStep { Id = x.Id + "#x", Name = "custom", Kind = PipelineStepKind.Other }] })] };
        SourceAnalysisReview.CiCdReason(other).Should().Be("2 steps not classified");
        Render<PipelineWorkspace>(r => r.Add(x => x.Evidence, SourceEvidenceDomainsUiTests.Evidence() with { CiCd = other })).Markup.Should().Contain("2 steps not classified", "the domain header shows the same reason as the Overview");
    }

    [Fact]
    public void Change_summaries_count_added_removed_and_changed_per_area()
    {
        SourceAnalysisReview.ChangeText(new("Tables", 2, 1, 0)).Should().Be("+2 added · −1 removed");
        SourceAnalysisReview.ChangeText(new("Tables", 0, 0, 3)).Should().Be("~3 changed");
        SourceAnalysisReview.FileName(@"src\Api\Program.cs").Should().Be("Program.cs");
        SourceAnalysisReview.Plural(1, "contract", "contracts").Should().Be("1 contract");
    }
}
