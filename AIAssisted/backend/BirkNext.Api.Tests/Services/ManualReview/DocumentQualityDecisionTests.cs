using BirkNext.ManualReview;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.ManualReview;

/// <summary>Document Quality Review findings use the shared manual-review store under their own module; nothing else changes.</summary>
public sealed class DocumentQualityDecisionTests
{
    [Fact]
    public void Document_quality_decisions_are_a_known_module_and_keep_the_shared_validation()
    {
        ManualReviewModules.All.Should().Contain(ManualReviewModules.DocumentQuality);
        var request = new ManualReviewDecisionRequest
        {
            Module = ManualReviewModules.DocumentQuality, ScopeKey = "artifact-pack:SkoleService/001-skole", SubjectKey = "qa-auditor|REQ-AMBIGUOUS|spec.md",
            Reviewer = "Test lead", Decision = ManualReviewDecisionValue.RiskAccepted, Note = "Accepted for this release",
        };
        ManualReviewRules.Validate(request).Should().BeNull();
        ManualReviewRules.Validate(request with { Note = "Bearer eyJabc" }).Should().NotBeNull("secrets are refused for every module");
        ManualReviewRules.Validate(request with { Module = "document-quality-x" }).Should().Be("Unknown review module.");
    }
}
