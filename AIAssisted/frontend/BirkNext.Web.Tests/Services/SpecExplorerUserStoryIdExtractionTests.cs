using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests for User Story ID extraction fallback used in candidates-only mode.
/// Verifies portability across real sample formats and test formats.
/// </summary>
public sealed class SpecExplorerUserStoryIdExtractionTests
{
    [Theory]
    [InlineData("User Story 1 — User Activated When Assigned to M2LB in Entra (Priority: P1)", "US-001")]
    [InlineData("User Story 2 — User Deactivated When Removed from Entra Scope (Priority: P1)", "US-002")]
    [InlineData("User Story 3 — Initial Full Synchronization on Adapter Startup (Priority: P2)", "US-003")]
    [InlineData("User Story 4 — Operations Team Monitors Provisioning Health (Priority: P3)", "US-004")]
    public void TryExtractUserStoryId_WithEMDashSeparator_ReturnsNormalizedId(string heading, string expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("User Story 1 - Developer Onboards and Runs the Service Locally (Priority: P1)", "US-001")]
    [InlineData("User Story 2 - Developer Verifies End-to-End Routing (Priority: P2)", "US-002")]
    [InlineData("User Story 10 - Operator Monitors Service Health (Priority: P3)", "US-010")]
    public void TryExtractUserStoryId_WithHyphenSeparator_ReturnsNormalizedId(string heading, string expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("User Story 1 Continuous Event Stream Processing", "US-001")]
    [InlineData("User Story 5: Security Classification Enforcement", "US-005")]
    [InlineData("User Story #1 with hash prefix", "US-001")]
    public void TryExtractUserStoryId_WithVariantSeparators_ReturnsNormalizedId(string heading, string expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("US1: API Surface", "US-001")]
    [InlineData("US2: Edge Cases", "US-002")]
    [InlineData("US-001: Full Spec", "US-001")]
    [InlineData("US-010: Multiple Digits", "US-010")]
    public void TryExtractUserStoryId_WithColonFormat_ReturnsNormalizedId(string heading, string expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("us1: lowercase", "US-001")]
    [InlineData("Us1: mixed case", "US-001")]
    [InlineData("USER STORY 1 — all caps", "US-001")]
    [InlineData("user story 5 lowercase", "US-005")]
    public void TryExtractUserStoryId_CaseInsensitive(string heading, string expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UC1: Use Case", "US-001")]
    [InlineData("UC-005: Use Case Five", "US-005")]
    public void TryExtractUserStoryId_SupportsUcAlias(string heading, string expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USability Design", null)]
    [InlineData("US123abc: invalid", null)]
    [InlineData("US1something: invalid", null)]
    [InlineData("User Scenario 1: not a story", null)]
    [InlineData("API Surface", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryExtractUserStoryId_InvalidFormats_ReturnsNull(string? heading, string? expected)
    {
        var result = SpecExplorerService.TryExtractUserStoryId(heading);
        result.Should().Be(expected);
    }

    [Fact]
    public void TryExtractUserStoryId_ZeroPadding_AlwaysThreeDigits()
    {
        var result1 = SpecExplorerService.TryExtractUserStoryId("US1: Title");
        result1.Should().Be("US-001");

        var result2 = SpecExplorerService.TryExtractUserStoryId("US10: Title");
        result2.Should().Be("US-010");

        var result3 = SpecExplorerService.TryExtractUserStoryId("US100: Title");
        result3.Should().Be("US-100");
    }

    [Fact]
    public void TryExtractUserStoryId_MultipleExamples_FromDifferentModules()
    {
        // autorisasjon module format
        SpecExplorerService.TryExtractUserStoryId("User Story 1 — User Activated When Assigned to M2LB in Entra (Priority: P1)")
            .Should().Be("US-001");

        // person-adapter module format
        SpecExplorerService.TryExtractUserStoryId("User Story 3 — Security Classification Enforcement (Priority: P1)")
            .Should().Be("US-003");

        // proxy module format (with hyphen instead of em-dash)
        SpecExplorerService.TryExtractUserStoryId("User Story 1 - Developer Onboards and Runs the Service Locally (Priority: P1)")
            .Should().Be("US-001");

        // Test data format
        SpecExplorerService.TryExtractUserStoryId("US1: API Surface")
            .Should().Be("US-001");
    }

    [Fact]
    public void TryExtractUserStoryId_Consistency_WithParserContract()
    {
        // Parser generates IDs as "US-NNN" format
        // Fallback should match this convention
        var testCases = new[]
        {
            ("User Story 1", "US-001"),
            ("User Story 2", "US-002"),
            ("US1:", "US-001"),
            ("US-001:", "US-001"),
        };

        foreach (var (input, expected) in testCases)
        {
            var result = SpecExplorerService.TryExtractUserStoryId(input + " Title");
            result.Should().Be(expected, $"Fallback should match parser contract for '{input}'");
        }
    }
}
