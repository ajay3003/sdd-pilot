using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Evidence states and the apply rules of Add-integration templates (pure presentation; nothing binds or saves).</summary>
public sealed class IntegrationTemplatePresentationTests
{
    private static readonly TestPackSourceRole Role = new("mu", "MU", "");
    private static TestPackSourceCandidate C(char fill, bool bound = false) => new(Guid.NewGuid(), $"{fill}.zip", new string(fill, 64), DateTimeOffset.UnixEpoch) { Bound = bound };

    [Fact]
    public void EvidenceStates_FollowTheIntegrationsOwnBindingFirst_ThenTheSourceRequirement()
    {
        var own = new IntegrationEvidenceBinding { IntegrationId = "x", ArchiveName = "a.zip" };
        IntegrationTemplatePresentation.Evidence(null, null).Should().Be(TemplateEvidenceState.NotRequired);
        IntegrationTemplatePresentation.Evidence(new(Role, TestPackSourceState.Missing), null).Should().Be(TemplateEvidenceState.Missing);
        IntegrationTemplatePresentation.Evidence(new(Role, TestPackSourceState.AvailableNotBound) { Candidates = [C('a')] }, null).Should().Be(TemplateEvidenceState.Available);
        IntegrationTemplatePresentation.Evidence(new(Role, TestPackSourceState.Bound) { Candidates = [C('a', bound: true)] }, null)
            .Should().Be(TemplateEvidenceState.NeedsBinding, "bound to another integration still needs this integration's own binding");
        IntegrationTemplatePresentation.Evidence(new(Role, TestPackSourceState.ReviewRequired) { Candidates = [C('a'), C('b')] }, null).Should().Be(TemplateEvidenceState.Ambiguous);
        IntegrationTemplatePresentation.Evidence(new(Role, TestPackSourceState.ReviewRequired) { Candidates = [C('a'), C('b')] }, own).Should().Be(TemplateEvidenceState.Bound);
        IntegrationTemplatePresentation.Label(TemplateEvidenceState.Available).Should().NotBe(IntegrationTemplatePresentation.Label(TemplateEvidenceState.Bound));
    }

    [Fact]
    public void Apply_FillsEmptyAndTemplateWrittenFields_AndReplacesPersonValuesOnlyOnReplace()
    {
        var template = new IntegrationTemplateView("t", "T", "", IntegrationKind.HttpApi, "p", "P")
        {
            Values = new Dictionary<string, IntegrationTemplateValue> { ["DisplayName"] = new("Template name", "Template"), ["Producer"] = new("Portal", "Template") },
        };
        var current = new Dictionary<string, string?> { ["DisplayName"] = "Mine", ["Producer"] = "Debezium", ["Kind"] = null };
        var written = new Dictionary<string, string> { ["Producer"] = "Debezium" }; // a platform default, not a person's value

        IntegrationTemplatePresentation.Conflicts(current, written, template).Should().Equal("DisplayName");
        IntegrationTemplatePresentation.Apply(current, written, template, TemplateApplyMode.WhereEmpty)
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["Kind"] = "HttpApi", ["Producer"] = "Portal" });
        IntegrationTemplatePresentation.Apply(current, written, template, TemplateApplyMode.Replace)["DisplayName"].Should().Be("Template name");
    }

    [Fact]
    public void EnvironmentType_IsTheProfileKind_AndCustomHasNone()
    {
        IntegrationTemplatePresentation.EnvironmentType(new FrontendAnalysisProfile { EnvironmentType = FrontendEnvironmentType.QA }).Should().Be("QA");
        IntegrationTemplatePresentation.EnvironmentType(new FrontendAnalysisProfile { EnvironmentType = FrontendEnvironmentType.Custom }).Should().BeNull();
    }
}
