using FluentAssertions;
using C = BirkNext.Web.Services.ScenarioCentricPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>Plan &amp; prepare polish (2026-11-20): step fields show their whole text, references read as file + anchor, ids are short.</summary>
public sealed class PlanPreparePolishTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("Open the page.", 1)]
    [InlineData("the child is not expected, even with an upper secondary education relationship; an accepted report for the child is counted as received", 2)]
    [InlineData("a\nb\nc", 3)]
    public void A_step_field_has_enough_rows_for_its_text_and_never_more_than_six(string? text, int rows) => C.Rows(text).Should().Be(rows);

    [Fact]
    public void A_long_rows_count_is_capped() => C.Rows(new string('x', 2000)).Should().Be(6);

    [Theory]
    [InlineData("Project/SkoleService/specs/001-skoleservice/spec.md#scenario-3", "spec.md#scenario-3")]
    [InlineData("skolenaervaer/duplikat-melding", "skolenaervaer/duplikat-melding")]
    [InlineData("a/b/c/feature", "c/feature")]
    public void A_reference_reads_as_its_file_and_anchor(string reference, string shown) => C.ShortReference(reference).Should().Be(shown);

    [Fact]
    public void A_snapshot_id_is_shown_short() => C.ShortId("025eb58c-3c62-4e35-b6f8-175a48ec83f0").Should().Be("025eb58c");
}
