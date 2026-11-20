using BirkNext.Api.Services.TestPlanning;
using BirkNext.SystemUnderstanding;
using FluentAssertions;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Audit 2026-11-20 ("Duplikat melding" in a Meldingsutveksler-only project): a cross-system guide recognised through one source role ("mu") offered
/// all its scenarios, although every one was evidenced only in another system's source ("m2lb"). A project is offered only the guide scenarios whose
/// evidence lies in the roles its source was recognised as; a scenario whose evidence names no role is kept (the guide does not say). Synthetic guide.
/// </summary>
public sealed class GuideScenarioSourceScopeTests
{
    private static GuideEvidence In(string role) => new() { Class = GuideEvidenceClass.NotAssessed, SourceRole = role };

    private static readonly SystemGuideContent Guide = new()
    {
        Title = "Synthetic journey",
        Steps =
        [
            new GuideStep { Id = "1", Title = "Receiver accepts", Evidence = [In("mu")] },
            new GuideStep { Id = "2", Title = "Service checks idempotency", Evidence = [In("app")] },
            new GuideStep { Id = "3", Title = "Undocumented step" },
        ],
        Rules = [new GuideRule { Id = "r-both", TestScenarioIds = ["via-rule"], Evidence = [In("mu")] }],
        Scenarios =
        [
            new GuideTestScenario { Id = "receiver", Title = "Receiver", StepIds = ["1"] },
            new GuideTestScenario { Id = "duplicate", Title = "Duplicate", StepIds = ["2"] },
            new GuideTestScenario { Id = "unknown", Title = "No role named", StepIds = ["3"] },
            new GuideTestScenario { Id = "via-rule", Title = "Rule in mu", StepIds = ["2"] },
        ],
    };

    private static readonly SystemGuideRevision Revision = new() { GuideKey = "synthetic", Revision = 1, State = GuideRevisionState.Draft, Content = Guide };

    [Fact]
    public void Only_scenarios_evidenced_in_the_recognised_roles_or_in_none_are_offered()
    {
        var mu = new HashSet<string>(StringComparer.Ordinal) { "mu" };
        D.FromGuide("import:x", Revision, mu).Select(s => s.Title).Should().BeEquivalentTo(["Receiver", "No role named", "Rule in mu"]);
        D.FromGuide("import:x", Revision, new HashSet<string> { "mu", "app" }).Should().HaveCount(4, "a project with both sources is offered every scenario");
        D.FromGuide("import:x", Revision).Should().HaveCount(4, "without role information nothing is filtered");
        D.ScenarioRoles(Guide, Guide.Scenarios[3]).Should().BeEquivalentTo(["app", "mu"]);
    }

    [Fact]
    public void The_capability_says_how_many_scenarios_belong_to_another_part_of_the_system()
    {
        var labels = new Dictionary<string, string> { ["mu"] = "MU", ["app"] = "The application" };
        SystemGuideScenarioProvider.Detail(Revision, new HashSet<string> { "mu" }, labels)
            .Should().Be("Synthetic journey: 3 of 4 scenarios apply to this project's source (MU); 1 is evidenced only in The application and is not offered here (revision 1, Draft)");
        SystemGuideScenarioProvider.Detail(Revision, new HashSet<string> { "mu", "app" }, labels).Should().Be("Synthetic journey: 4 scenarios (revision 1, Draft)");
    }
}
