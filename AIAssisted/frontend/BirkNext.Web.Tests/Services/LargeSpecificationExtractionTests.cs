using System.Diagnostics;
using System.Text;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Real M2LB service specifications are long (SkoleService 92 000 characters, HendelseService 98 000): the Specification Explorer said "too large
/// for deterministic review analysis" for four of them under the old 50 000-character bound. The extraction is linear, so the default bound is
/// the 1 MB of a specification import, and a spec of that order is analysed.
/// </summary>
public sealed class LargeSpecificationExtractionTests
{
    private static string Spec(int targetChars)
    {
        var text = new StringBuilder("# Feature Specification: Skoleservice\n\n## User Scenarios & Testing\n\n");
        for (var story = 1; text.Length < targetChars; story++)
        {
            text.Append($"### User Story {story} - Rapportering av fravær for elev {story} (Priority: P1)\n\n");
            text.Append($"Skoletjenesten mottar en periodesammendrag for eleven og registrerer fraværet for perioden {story}.\n\n");
            text.Append("**Acceptance Scenarios**:\n\n");
            for (var s = 1; s <= 3; s++)
                text.Append($"{s}. **Given** en elev {story}-{s} er registrert, **When** skolen sender rapporten, **Then** fraværet lagres for perioden {story}-{s}.\n");
            text.Append($"\n- **FR-{story:000}**: The system MUST store the absence summary of report {story} once.\n\n");
        }
        return text.ToString();
    }

    [Fact]
    public void The_default_bound_reads_the_largest_real_specifications_and_matches_the_import_limit()
    {
        new ExtractionConfiguration().MaxInputLengthChars.Should().Be(ExtractionConfiguration.DefaultMaxInputLengthChars).And.BeGreaterThan(98_078 * 4);
        ExtractionConfiguration.DefaultMaxInputLengthChars.Should().Be(1_000_000, "a specification file import accepts up to 1 MB");
    }

    [Fact]
    public async Task A_spec_of_twice_the_largest_M2LB_specification_is_analysed()
    {
        var text = Spec(200_000);
        var sw = Stopwatch.StartNew();
        var result = await new ScenarioExtractionService(new ExtractionConfiguration()).ExtractAsync(text, ExtractionProfile.Speckit);
        sw.Stop();
        result.Status.Should().Be(PipelineStatus.Success);
        result.InputLengthChars.Should().Be(text.Length);
        result.Candidates.Should().NotBeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000, "the extraction is linear in the input");
    }

    [Fact]
    public async Task Beyond_the_bound_it_still_says_too_large_with_the_size()
    {
        var result = await new ScenarioExtractionService(new ExtractionConfiguration { MaxInputLengthChars = 1_000 }).ExtractAsync(Spec(2_000), ExtractionProfile.Speckit);
        result.Status.Should().Be(PipelineStatus.InputTooLarge);
        result.InputLengthChars.Should().BeGreaterThan(1_000);
    }
}
