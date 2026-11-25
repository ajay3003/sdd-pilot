using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Overfør testplan (2026-11-25): the exporter's check messages in plain Norwegian, with where to fix a project default; unknown ones kept.</summary>
public sealed class AdoIssueTextTests
{
    [Fact]
    public void A_missing_Area_Path_is_explained_in_Norwegian_and_points_to_the_settings()
    {
        var said = AdoIssueText.Explain("Azure DevOps Area Path is missing (required for Azure DevOps Services), for example MyProject\\MyArea.");
        said.Text.Should().StartWith("Område (Area Path) mangler.");
        said.FixInSettings.Should().BeTrue();
        said.Translated.Should().BeTrue();
        AdoIssueText.SettingsLink.Should().Be("admin/system-settings?section=azure-devops-test-plans");
    }

    [Fact]
    public void A_message_without_a_translation_is_shown_as_it_is_never_hidden()
    {
        var said = AdoIssueText.Explain("Something new from the exporter.");
        said.Should().Be(new AdoIssueText.Explained("Something new from the exporter.", false, false));
        AdoIssueText.Explain("No test case is ready for export. Select cases.").FixInSettings.Should().BeFalse("not a settings problem");
    }
}
