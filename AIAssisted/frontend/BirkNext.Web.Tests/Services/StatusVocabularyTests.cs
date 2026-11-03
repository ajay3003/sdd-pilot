using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>The global status vocabulary: one definition per term, stable intents, safe fallback, and a dictionary document that cannot drift.</summary>
public sealed class StatusVocabularyTests : BunitContext
{
    [Fact]
    public void EveryStatusHasExactlyOneDefinition()
    {
        var keys = StatusVocabulary.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(Enum.GetValues<SemanticStatus>(), s => Assert.Contains(s, keys));
        Assert.All(StatusVocabulary.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Label));
            Assert.False(string.IsNullOrWhiteSpace(d.Meaning));
            Assert.False(string.IsNullOrWhiteSpace(d.UseWhen));
            Assert.False(string.IsNullOrWhiteSpace(d.DoNotUseWhen));
        });
    }

    [Fact]
    public void LabelsAreUniqueAndSentenceCase()
    {
        var labels = StatusVocabulary.All.Select(d => d.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(labels, l => Assert.NotEqual(l.ToUpperInvariant(), l));
    }

    [Theory]
    [InlineData(SemanticStatus.Ready, StatusIntent.Positive)]
    [InlineData(SemanticStatus.Verified, StatusIntent.Positive)]
    [InlineData(SemanticStatus.NeedsReview, StatusIntent.Warning)]
    [InlineData(SemanticStatus.Blocked, StatusIntent.Danger)]
    [InlineData(SemanticStatus.Conflict, StatusIntent.Danger)]
    [InlineData(SemanticStatus.NotAssessed, StatusIntent.Neutral)]
    [InlineData(SemanticStatus.Unknown, StatusIntent.Neutral)]
    public void IntentsAreStable(SemanticStatus status, StatusIntent intent) => Assert.Equal(intent, StatusVocabulary.Intent(status));

    [Fact]
    public void RedIsReservedForBlockingAndFailureStates()
    {
        var danger = StatusVocabulary.All.Where(d => d.Intent == StatusIntent.Danger).Select(d => d.Key).ToHashSet();
        Assert.DoesNotContain(SemanticStatus.NeedsReview, danger);
        Assert.DoesNotContain(SemanticStatus.Missing, danger);
        Assert.DoesNotContain(SemanticStatus.NotDetected, danger);
        Assert.DoesNotContain(SemanticStatus.Unknown, danger);
    }

    [Theory]
    [InlineData("Ready to run", SemanticStatus.Ready)]
    [InlineData("  needs REVIEW ", SemanticStatus.NeedsReview)]
    [InlineData("Not configured", SemanticStatus.Missing)]
    [InlineData("Confirmed in source", SemanticStatus.Confirmed)]
    [InlineData("something-new-from-an-old-snapshot", SemanticStatus.Unknown)]
    [InlineData(null, SemanticStatus.Unknown)]
    public void LegacyValuesMapSafely(string? legacy, SemanticStatus expected) => Assert.Equal(expected, StatusVocabulary.FromLegacy(legacy));

    [Fact]
    public void UnknownNeverLooksReadyOrAlarming()
    {
        var unknown = StatusVocabulary.Of(StatusVocabulary.FromLegacy("garbage"));
        Assert.Equal(StatusIntent.Neutral, unknown.Intent);
    }

    [Fact]
    public void SemanticBadgeRendersCanonicalLabelIntentAndMeaning()
    {
        var cut = Render<SemanticStatusBadge>(p => p.Add(x => x.Status, SemanticStatus.NeedsReview).Add(x => x.TestId, "b"));
        var badge = cut.Find("[data-testid='b']");
        Assert.Contains("ssb-warning", badge.ClassName);
        Assert.Contains("Needs review", badge.TextContent);
        Assert.Contains(StatusVocabulary.Tooltip(SemanticStatus.NeedsReview), badge.GetAttribute("title") ?? "");
    }

    [Fact]
    public void QualifiedLabelKeepsTheCanonicalMeaning()
    {
        var cut = Render<SemanticStatusBadge>(p => p.Add(x => x.Status, SemanticStatus.NotDetected)
            .Add(x => x.QualifiedLabel, "Not detected in this snapshot").Add(x => x.TestId, "b"));
        var badge = cut.Find("[data-testid='b']");
        Assert.Contains("Not detected in this snapshot", badge.TextContent);
        Assert.Contains(StatusVocabulary.Tooltip(SemanticStatus.NotDetected), badge.GetAttribute("title") ?? "");
    }

    [Theory]
    [InlineData(BadgeKind.Type, "OpenAPI")]
    [InlineData(BadgeKind.Environment, "Development")]
    [InlineData(BadgeKind.Provenance, "Source Analysis")]
    [InlineData(BadgeKind.Confidence, "High confidence")]
    public void MetadataBadgesNeverUseStatusColour(BadgeKind kind, string text)
    {
        var cut = Render<MetaBadge>(p => p.Add(x => x.Kind, kind).Add(x => x.Text, text).Add(x => x.TestId, "m"));
        var badge = cut.Find("[data-testid='m']");
        Assert.DoesNotContain("ssb", badge.ClassName);
        Assert.Contains($"mb-{kind.ToString().ToLowerInvariant()}", badge.ClassName);
        Assert.Contains(text, badge.TextContent);
    }

    [Fact]
    public void ProvenanceBadgeReadsAsOrigin()
    {
        var cut = Render<MetaBadge>(p => p.Add(x => x.Kind, BadgeKind.Provenance).Add(x => x.Text, "Source Analysis"));
        Assert.Equal("From Source Analysis", cut.Find(".mb").TextContent.Trim());
    }

    [Fact]
    public void DictionaryDocumentMatchesTheCode()
    {
        var path = Path.Combine(AiAssistedRoot(), "docs", "status-vocabulary.md");
        var expected = StatusVocabulary.ToMarkdown();
        if (Environment.GetEnvironmentVariable("BIRKNEXT_REGENERATE_DOCS") == "1") File.WriteAllText(path, expected);
        Assert.True(File.Exists(path), "docs/status-vocabulary.md is missing — run the tests with BIRKNEXT_REGENERATE_DOCS=1.");
        Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n"));
        Assert.All(StatusVocabulary.All, d => Assert.Contains($"| {d.Label} |", expected));
    }

    private static string AiAssistedRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "frontend")) && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
        throw new DirectoryNotFoundException("AIAssisted root not found.");
    }
}
