using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using R = BirkNext.Web.Services.ProjectEnvironmentResolution;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// R7: project-scoped facts (key <c>import:{importId}</c>) and environment-scoped facts (Target Environment profile id) are related only through the
/// environment's own project binding — never merged by name, and an environment bound to another project never lends its results to this one.
/// </summary>
public sealed class ProjectEnvironmentResolutionTests
{
    private static FrontendAnalysisProfile Env(string id, string name, string? importId = null, string? projectName = null) => new()
    {
        Id = id, Name = name,
        Project = importId is null && projectName is null ? null : new EnvironmentProject { ProjectImportId = importId, ProjectName = projectName ?? "" },
    };

    [Fact]
    public void An_environment_bound_to_the_selected_project_resolves_without_a_notice()
    {
        var dev = Env("dev", "DEV", "imp-1", "LendoPlus");
        var r = R.Resolve("import:imp-1", dev, [dev]);

        (r.State, r.EnvironmentId, r.EnvironmentFactsApply, r.Notice).Should().Be((R.State.Bound, "dev", true, (string?)null));
    }

    [Fact]
    public void An_unbound_environment_is_used_but_labelled_unconfirmed()
    {
        var r = R.Resolve("import:imp-1", Env("dev", "DEV"), []);

        r.State.Should().Be(R.State.Unbound);
        r.EnvironmentFactsApply.Should().BeTrue();
        r.Notice.Should().Contain("not bound to a project").And.Contain("Target Environments › Project");
    }

    [Fact]
    public void An_environment_bound_to_another_project_contributes_nothing_and_names_the_right_environments()
    {
        var m2lb = Env("qa", "QA", "imp-2", "M2LB");
        var lendo = Env("dev", "DEV", "imp-1", "LendoPlus");
        var r = R.Resolve("import:imp-1", m2lb, [m2lb, lendo]);

        r.State.Should().Be(R.State.OtherProject);
        r.EnvironmentFactsApply.Should().BeFalse("M2LB's relationship analysis is not LendoPlus's");
        r.Notice.Should().Contain("bound to M2LB").And.Contain("Environments bound to this project: DEV");
    }

    [Fact]
    public void Identities_are_never_merged_by_name()
    {
        var sameName = Env("qa", "QA", "imp-9", "LendoPlus");
        R.Resolve("import:imp-1", sameName, [sameName]).State.Should().Be(R.State.OtherProject, "a project name is not a project identity");
        R.Resolve("project:LendoPlus", sameName, [sameName]).State.Should().Be(R.State.OtherProject, "only import keys resolve to a binding");
    }

    [Fact]
    public void Without_a_project_or_an_environment_the_state_says_so()
    {
        R.Resolve(null, Env("dev", "DEV", "imp-1"), []).State.Should().Be(R.State.NoProject);
        var none = R.Resolve("import:imp-1", null, []);
        (none.State, none.EnvironmentFactsApply).Should().Be((R.State.NoEnvironment, false));
        R.ImportIdOf("import:abc").Should().Be("abc");
        R.ImportIdOf("abc").Should().BeNull();
    }
}
