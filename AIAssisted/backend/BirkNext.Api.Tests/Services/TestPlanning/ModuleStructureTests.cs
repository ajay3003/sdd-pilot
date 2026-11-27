using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Project-level audit (2026-11-27): Testoversikt's «Oversikt per modul» uses the canonical module rule (the explorers' rule, now shared). The paths
/// are the real M2LB package structure (39 spec.md files, module docs, root specs/ and project docs), not invented classifications.
/// </summary>
public sealed class ModuleStructureTests
{
    private static readonly string[] M2lb =
    [
        "AutorisasjonService/specs/001-translate-source-english/spec.md",
        "HendelseService/specs/001-hendelsestjenesten/spec.md",
        "HendelseService/docs/Hendelsestjenesten-—-Testspesifikasjon.md",
        "HendelseService/docs/Hendelsestjenesten-—-Brukerhistorier-og-funksjonelle-krav.md",
        "RevisjonService/specs/001-revisjonstjenesten-m01/spec.md",
        "RevisjonService/docs/M2LB.RevisjonService-—-Test-Specification.md",
        "specs/001-platform-contract-rename/spec.md",
        "specs/002-operation-display-name/hand-off.md",
        "docs/analysis/skolenaervaer.md",
        "docs/operations/skolenaervaer-pr.md",
    ];

    private static readonly IReadOnlySet<string> Roots = ModuleStructure.RootsOf(M2lb);

    [Theory]
    [InlineData("AutorisasjonService/specs/001-translate-source-english/spec.md", "AutorisasjonService")]                 // 1. a genuine module scenario
    [InlineData("specs/001-platform-contract-rename/spec.md", ModuleStructure.ProjectWide)]                                // 2. explicitly project-wide
    [InlineData("specs/002-operation-display-name/hand-off.md", ModuleStructure.ProjectWide)]                              //    a supporting document of it
    [InlineData("docs/analysis/skolenaervaer.md", ModuleStructure.Unassigned)]                                             // 3. no module association
    [InlineData("docs/operations/skolenaervaer-pr.md", ModuleStructure.Unassigned)]
    [InlineData("HendelseService/docs/Hendelsestjenesten-—-Testspesifikasjon.md", "HendelseService")]                      // 5. TEST-E without a user story
    [InlineData("HendelseService/docs/Hendelsestjenesten-—-Brukerhistorier-og-funksjonelle-krav.md", "HendelseService")]   //    a module's requirements
    [InlineData("RevisjonService/docs/M2LB.RevisjonService-—-Test-Specification.md", "RevisjonService")]
    public void Each_document_gets_its_canonical_module_or_one_of_the_two_groups_outside_the_modules(string path, string expected)
    {
        // Before (2026-11-27) every path whose second folder was not «specs» became «Prosjektnivå» — the module documents above included.
        ModuleStructure.OverviewModule(path, Roots).Should().Be(expected);
        ModuleStructure.WhyOverviewModule(path, Roots).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_roots_are_the_spec_kit_modules_and_the_project_root_and_nothing_is_lost()
    {
        Roots.Should().BeEquivalentTo(["AutorisasjonService", "HendelseService", "RevisjonService", ""]);
        // 10. Every document lands in exactly one group: no duplication, no loss.
        M2lb.Select(p => ModuleStructure.OverviewModule(p, Roots)).Should().HaveCount(M2lb.Length).And.NotContainNulls();
        M2lb.GroupBy(p => ModuleStructure.OverviewModule(p, Roots)).Sum(g => g.Count()).Should().Be(M2lb.Length);
    }

    [Fact]
    public void A_nested_module_is_its_own_module_and_a_document_without_structure_is_never_guessed_into_one()
    {
        // 4. A cross-module document is not attributed to a module by its name: only its folder decides.
        var roots = ModuleStructure.RootsOf(["Platform/Api/specs/001-x/spec.md", "Platform/specs/001-y/spec.md"]);
        ModuleStructure.OverviewModule("Platform/Api/docs/flow-across-Person-and-Skole.md", roots).Should().Be("Platform/Api", "the deepest root");
        ModuleStructure.OverviewModule("Person-and-Skole-cross-module-flow.md", roots).Should().Be(ModuleStructure.Unassigned, "a file name never decides");
        ModuleStructure.OverviewModule("", roots).Should().Be(ModuleStructure.Unassigned);
    }

    [Fact]
    public void The_explorers_use_the_same_rule()
    {
        foreach (var p in M2lb)
            ModuleStructure.ModuleOf(p, Roots).Should().Be(ModuleStructure.OverviewModule(p, Roots) is ModuleStructure.ProjectWide or ModuleStructure.Unassigned ? "" : ModuleStructure.OverviewModule(p, Roots));
    }
}
