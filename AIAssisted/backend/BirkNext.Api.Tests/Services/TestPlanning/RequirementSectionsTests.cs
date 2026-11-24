using System.Runtime.CompilerServices;
using BirkNext.Sdd;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// FR functional areas from the specification's own structure (2026-11-24). Real M2LB specifications (copied unchanged into Fixtures/m2lb-specs)
/// plus clearly synthetic edge cases. No section is invented; FR-015 and FR-015a stay distinct; the source spelling is kept for display.
/// </summary>
public sealed class RequirementSectionsTests
{
    internal static string Spec(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "m2lb-specs", relative));

    private static readonly Lazy<RequirementSectionMap> Skole = new(() => RequirementSections.Read(Spec("SkoleService/specs/001-skoleservice/spec.md")));

    [Fact]
    public void S01_real_SkoleService_bold_sections_are_the_functional_areas_in_source_order()
    {
        Skole.Value.Sections.Select(s => s.Title).Should().Equal(
            "Intake and identity", "Classification", "Validity control (reject with machine-readable reason)", "Quality control (accept and flag)",
            "Corrections, chains and idempotency", "Period lock", "Expected population and coverage", "Delivery to Bufdata", "Education master data (Utdanning)",
            "Follow-up lists", "Access, audit and retention");
    }

    [Fact]
    public void S02_each_requirement_belongs_to_the_section_it_is_written_under()
    {
        Skole.Value.Find("FR-001")!.Section.Should().Be("Intake and identity");
        Skole.Value.Find("FR-015a")!.Section.Should().Be("Quality control (accept and flag)");
        Skole.Value.Find("FR-014")!.Section.Should().Be("Quality control (accept and flag)");
        Skole.Value.Sections.Single(s => s.Title == "Quality control (accept and flag)").RequirementIds.Should().Contain(["FR-014", "FR-015", "FR-015A", "FR-016"]);
    }

    [Fact]
    public void S03_FR_015_and_FR_015a_stay_distinct_and_the_suffix_keeps_its_source_spelling()
    {
        var plain = Skole.Value.Find("FR-015")!; var suffixed = Skole.Value.Find("FR-015a")!;
        plain.RequirementId.Should().Be("FR-015");
        suffixed.RequirementId.Should().Be("FR-015A", "the canonical comparison key (unchanged identity)");
        suffixed.DisplayId.Should().Be("FR-015a", "the exact source spelling, for display");
        Skole.Value.DisplayId("fr-015A").Should().Be("FR-015a");
        suffixed.Line.Should().BeGreaterThan(plain.Line);
    }

    [Fact]
    public void S04_a_source_reference_inside_a_requirement_is_not_a_requirement()
    {
        Skole.Value.Requirements.Should().NotContain(r => r.DisplayId.StartsWith("SR-", StringComparison.OrdinalIgnoreCase), "[SR-FR-001] cites a source requirement");
        Skole.Value.Requirements.Should().OnlyHaveUniqueItems(r => r.RequirementId);
    }

    [Fact]
    public void S05_real_Person_bold_id_paragraphs_without_sections_have_an_unknown_area_never_an_invented_one()
    {
        var person = RequirementSections.Read(Spec("PersonService/specs/001-person-module/spec.md"));
        person.Requirements.Should().NotBeEmpty();
        person.Find("FR-001")!.Section.Should().BeNull("the specification gives these requirements no section");
        person.Sections.Should().NotContain(s => s.Title.StartsWith("FR-", StringComparison.Ordinal), "a bold requirement id is never a section");
    }

    [Fact]
    public void S06_real_SkoleAdapter_and_MeldingsutvekslerReceiver_sections_follow_the_same_structure_with_their_own_titles()
    {
        var adapter = RequirementSections.Read(Spec("SkoleAdapter/specs/001-skoleadapter/spec.md"));
        adapter.Find("FR-001")!.Section.Should().Be("Consumption");
        adapter.Sections.Select(s => s.Title).Should().Contain("Field minimisation and translation");
        var receiver = RequirementSections.Read(Spec("MeldingsutvekslerReceiver/specs/001-meldingsutveksler-receiver/spec.md"));
        receiver.Find("FR-001a")!.Section.Should().Be("Consumption and decryption");
        receiver.Find("FR-001a")!.DisplayId.Should().Be("FR-001a");
    }

    [Fact]
    public void S07_markdown_headings_nested_headings_empty_sections_and_FRs_outside_sections_synthetic()
    {
        const string md = """
            # Spec
            ## Requirements
            FR-000: Before any section.
            ### Intake
            - **FR-001**: Accept.
            #### Identity
            - FR-002: Resolve the child.
            ### Empty area
            ### Quality
            **Flags**
            - **FR-003**: Flag.
            **Note**: not a section, a label with text.
            - **FR-004**: Still under Flags.
            ## Success Criteria
            - **FR-099**: Not in the requirements region.
            """;
        var map = RequirementSections.Read(md);
        map.Find("FR-000")!.Section.Should().BeNull();
        map.Find("FR-001")!.Section.Should().Be("Intake");
        map.Find("FR-002")!.SectionPath.Should().Equal("Intake", "Identity");
        map.Find("FR-003")!.SectionPath.Should().Equal("Quality", "Flags");
        map.Find("FR-004")!.Section.Should().Be("Flags", "a bold label with text after it never starts a section");
        map.Sections.Single(s => s.Title == "Empty area").RequirementIds.Should().BeEmpty();
        map.Find("FR-099").Should().BeNull("outside the requirements region");
    }

    [Fact]
    public void S08_no_requirements_region_or_empty_text_gives_nothing()
    {
        RequirementSections.Read("# Spec\n- **FR-001**: x").Requirements.Should().BeEmpty();
        RequirementSections.Read(null).Should().BeSameAs(RequirementSectionMap.Empty);
    }
}
