using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// «Barnet finnes ikke» (2026-11-26): a guide scenario with no specification file of its own is attributed to the specification folder its cited
/// files lie in — never by its title, never by source code or test paths, never when two folders disagree.
/// </summary>
public sealed class SpecificationFolderEvidenceTests
{
    // The real references of the M2LB scenario (isolated walkthrough database, 2026-11-26).
    private static readonly string[] Real =
    [
        "skolenaervaer/manglende-barn",
        "M2LB.zip (AIAssisted/testdata; archived 2026-10-28):SkoleService/specs/001-skoleservice/contracts/rapportinntak.md:lines 46, 51, 54",
        "M2LB.zip (AIAssisted/testdata; archived 2026-10-28):SkoleService/src/M2LB.SkoleService.Api/Rest/ReportIntake/ReportIntakeHandler.cs:lines 52, 58-59, 106, 109",
        "M2LB.zip (AIAssisted/testdata; archived 2026-10-28):SkoleService/tests/M2LB.SkoleService.Integration/Held/HeldReportTests.cs:lines 42-53, 61-69, 88-97",
        "M2LB (5).zip (historical guide citation):Skole/src/M2LB.Skole.Api/Rest/Rapportinntak/Rapportinntaksbehandler.cs:lines 18-22, 64-70, 105-107",
    ];

    [Fact]
    public void The_cited_contract_inside_a_specification_folder_names_that_specification()
    {
        SpecificationFolderEvidence.SpecificationOf(Real).Should().Be("SkoleService/specs/001-skoleservice/spec.md");
        BirkNext.Api.Services.TestPlanning.TestPlanSuggestionService.ModuleOf(SpecificationFolderEvidence.SpecificationOf(Real)!).Should().Be("SkoleService");
    }

    [Fact]
    public void Source_code_test_paths_titles_and_disagreeing_folders_never_decide_it()
    {
        SpecificationFolderEvidence.SpecificationOf([Real[0], Real[2], Real[3], Real[4], "Barnet finnes ikke"]).Should().BeNull("no cited file lies in a specification folder");
        SpecificationFolderEvidence.SpecificationOf([Real[1], "x.zip:Frontend/specs/002-child-search-profile/contracts/api.md:lines 1"]).Should().BeNull("two specifications disagree");
        SpecificationFolderEvidence.SpecificationOf([Real[1], Real[1].Replace("lines 46", "lines 50")]).Should().NotBeNull("the same folder twice agrees");
        SpecificationFolderEvidence.SpecificationOf([@"C:\pkg\SkoleService\specs\001-skoleservice\plan.md"]).Should().Be("SkoleService/specs/001-skoleservice/spec.md");
        SpecificationFolderEvidence.SpecificationOf([]).Should().BeNull();
    }
}
