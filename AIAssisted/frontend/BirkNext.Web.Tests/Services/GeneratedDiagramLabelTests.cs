using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Group 3: generated Mermaid labels showed escaped text raw ("Validate\nreport", "&amp;quot;id&amp;quot;"). Labels are turned into
/// plain text — never interpreted as HTML — without changing their meaning.</summary>
public sealed class GeneratedDiagramLabelTests
{
    [Theory]
    [InlineData("Validate\\nreport", "Validate report")]
    [InlineData("Receive<br/>message", "Receive message")]
    [InlineData("&quot;Fravaer&quot; &amp; status", "\"Fravaer\" & status")]
    [InlineData("<script>x</script>", "‹script›x‹/script›")]
    [InlineData("  \"Quoted   label\"  ", "Quoted label")]
    public void Labels_are_plain_readable_text(string raw, string expected) => GeneratedDiagrams.Clean(raw).Should().Be(expected);

    [Fact]
    public void A_sequence_message_with_an_escaped_newline_reads_as_one_message()
    {
        var diagram = GeneratedDiagrams.Read("sequenceDiagram\n    participant A as Adapter\n    participant S as Service\n    A->>S: Validate\\nreport\n");

        diagram.Should().NotBeNull();
        diagram!.Steps.Should().ContainSingle().Which.Message.Should().Be("Validate report");
    }
}
