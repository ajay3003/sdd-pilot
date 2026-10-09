using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Responsibility rules are judged on dependency evidence from the architecture extractor (resolved client targets, client/configuration
/// references), never on file or component names alone, and are Not assessed when the source is missing.
/// </summary>
public sealed class ArchitectureRuleEvaluatorTests
{
    private static readonly IReadOnlyList<ArchitectureResponsibilityRule> Rules = new SkoleNaervaerJourneyPack(new IntegrationJourneyHarness.FakeMessageFlows()).Rules;

    private static ArchitectureEvidence Evidence(string file, int line, string symbol) =>
        new(ArchitectureEvidenceKind.ApplicationSource, file, line, symbol, "http-client-wiring", "Typed HTTP client registered with AddHttpClient");

    private static IqrSourceSnapshot Snapshot(ArchitectureSnapshot? architecture) => new() { Archive = new("source.zip", new string('c', 64), 10), Architecture = architecture };

    private static ArchitectureComponent Component(string id, string name) => new() { Id = id, Name = name, ComponentType = ArchitectureComponentType.Api };

    private static ArchitectureRuleFinding Finding(ArchitectureRuleReport report, string ruleId) => report.Findings.Single(finding => finding.RuleId == ruleId);

    [Fact]
    public void WithoutSource_EveryRuleIsNotAssessed()
    {
        var report = ArchitectureRuleEvaluator.Evaluate("skolenaervaer", Rules, null);
        report.Findings.Should().HaveCount(4).And.OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed);
        ArchitectureRuleEvaluator.Evaluate("skolenaervaer", Rules, Snapshot(null)).Findings.Should().OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed);
    }

    [Fact]
    public void Dependencies_DecideTheOutcome_WithCitedSourceEvidence()
    {
        var architecture = new ArchitectureSnapshot
        {
            Components =
            [
                Component("mm", "Skolenaervaer.MM.Intake"), Component("adapter", "M2LB.SkoleAdapter.Worker"), Component("utdanning", "Utdanning.Api"),
                Component("person", "Person.Api"),
            ],
            Dependencies =
            [
                new() { Id = "d1", FromComponentId = "mm", ToId = "person", DependencyType = ArchitectureDependencyType.Http, Evidence = [Evidence("MM/Program.cs", 42, "AddHttpClient<IPersonClient>")] },
                new() { Id = "d2", FromComponentId = "adapter", ToId = "utdanning", DependencyType = ArchitectureDependencyType.Http, Evidence = [Evidence("Adapter/Program.cs", 10, "AddHttpClient<IUtdanningClient>")] },
                new() { Id = "d3", FromComponentId = "utdanning", TargetReference = "PersonGraphQlClient", DependencyType = ArchitectureDependencyType.GraphQl, Evidence = [Evidence("Utdanning/Program.cs", 7, "AddPersonGraphQlClient")] },
            ],
        };
        var report = ArchitectureRuleEvaluator.Evaluate("skolenaervaer", Rules, Snapshot(architecture));

        var mm = Finding(report, "mm-no-person");
        mm.Outcome.Should().Be(ArchitectureRuleOutcome.PotentialDeviation);
        mm.Detail.Should().Contain("not a confirmed violation");
        mm.Evidence.Should().ContainSingle(item => item.File == "MM/Program.cs" && item.Line == 42);
        Finding(report, "skoleadapter-no-person").Outcome.Should().Be(ArchitectureRuleOutcome.Conforms);
        Finding(report, "utdanning-person").Outcome.Should().Be(ArchitectureRuleOutcome.Conforms);
        Finding(report, "utdanning-person").Evidence.Should().ContainSingle(item => item.Symbol == "AddPersonGraphQlClient");
        Finding(report, "rapport-person").Outcome.Should().Be(ArchitectureRuleOutcome.NotAssessed, "Skolenærværsrapport source is not in the snapshot");
        report.Findings.Should().OnlyContain(finding => finding.Provenance.Contains("source.zip"));
    }

    [Fact]
    public void NameSimilarityAlone_NeverCountsAsADependency()
    {
        var architecture = new ArchitectureSnapshot
        {
            Components = [Component("mm", "MM.Intake"), Component("docs", "PersonLookupDocs"), Component("rapport", "Skolenærværsrapport.Api")],
            Dependencies = [new() { Id = "d1", FromComponentId = "mm", ToId = "mapping", TargetReference = "MappingProfile", DependencyType = ArchitectureDependencyType.ProjectReference }],
        };
        var report = ArchitectureRuleEvaluator.Evaluate("skolenaervaer", Rules, Snapshot(architecture));

        Finding(report, "mm-no-person").Outcome.Should().Be(ArchitectureRuleOutcome.Conforms, "a Person-named component that MM does not depend on is irrelevant");
        Finding(report, "rapport-person").Outcome.Should().Be(ArchitectureRuleOutcome.PotentialDeviation, "no Person dependency evidence exists for the report service");
        Finding(report, "rapport-person").Evidence.Should().BeEmpty();
    }

    [Fact]
    public void UnattributableOutboundCalls_MakeTheRuleNotAssessed_InsteadOfGuessing()
    {
        var architecture = new ArchitectureSnapshot
        {
            Components = [Component("adapter", "SkoleAdapter.Worker")],
            Dependencies = [new() { Id = "d1", FromComponentId = "adapter", DependencyType = ArchitectureDependencyType.Http }],
        };
        Finding(ArchitectureRuleEvaluator.Evaluate("skolenaervaer", Rules, Snapshot(architecture)), "skoleadapter-no-person").Outcome
            .Should().Be(ArchitectureRuleOutcome.NotAssessed);
    }

    [Theory]
    [InlineData("Skolenærværsrapport", "Skolenaervaersrapport.Api", true)]
    [InlineData("MM", "Skolenaervaer.MM.Worker", true)]
    [InlineData("MM", "Common.Mmapper", false)]
    [InlineData("Person", "IPersonClient", true)]
    [InlineData("Person", "Personalia.Api", false)]
    public void Tokens_MatchNameSegmentsOrCamelCaseWords(string token, string name, bool expected) =>
        ArchitectureRuleEvaluator.MatchesAny([token], name).Should().Be(expected);
}
