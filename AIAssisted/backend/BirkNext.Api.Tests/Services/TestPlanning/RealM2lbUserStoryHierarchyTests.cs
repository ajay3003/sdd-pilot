using System.IO.Compression;
using System.Text;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Opt-in check on the real M2LB archive (set BIRKNEXT_M2LB_MANUAL_ARCHIVE; skipped otherwise; nothing from the archive is committed): the shared
/// specification parser's Feature → User Story → Acceptance Scenario reading of every module's spec.md. Expectations are read from the source's
/// own structure (story headings, scenario text), never a guessed parent: SkoleService's "child past compulsory school age" scenario sits under
/// User Story 6; the Norwegian Frontend dashboard stories have their Gitt/naar/saa scenarios; Hendelse's "Scenario N:" headings are scenarios
/// without a story; an in-text story reference ([US-A3]) is never a parent.
/// </summary>
public sealed class RealM2lbUserStoryHierarchyTests(ITestOutputHelper output)
{
    private const string Variable = "BIRKNEXT_M2LB_MANUAL_ARCHIVE";

    private static Dictionary<string, (string Text, BirkNext.Web.Models.SpecificationSemanticModel Model)>? Specs()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } archive || !File.Exists(archive)) return null;
        using var zip = ZipFile.OpenRead(archive);
        var result = new Dictionary<string, (string, BirkNext.Web.Models.SpecificationSemanticModel)>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(e => e.FullName.Replace('\\', '/').EndsWith("/spec.md", StringComparison.Ordinal) && e.FullName.Contains("/specs/0", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();
            result[entry.FullName.Replace('\\', '/')] = (text, SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(text), text));
        }
        return result;
    }

    [Fact]
    public void The_real_specifications_read_as_feature_story_scenario()
    {
        if (Specs() is not { } specs) { output.WriteLine($"Skipped: {Variable} is not set."); return; }
        foreach (var (path, (_, model)) in specs.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var stories = model.UserStories.Where(u => u.IsStoryHeading).ToList();
            var parented = model.AcceptanceScenarios.Count(s => s.ParentStoryKey is not null);
            output.WriteLine($"{path}: {stories.Count} stories ({stories.Count(s => s.Priority is not null)} with priority, {stories.Count(s => s.IndependentTest is not null)} with Independent Test), {model.AcceptanceScenarios.Count} scenarios, {parented} under a story");
            // A parent is always one of the document's own story headings.
            model.AcceptanceScenarios.Where(s => s.ParentStoryKey is not null).Should().OnlyContain(s => stories.Any(u => u.StableKey == s.ParentStoryKey), path);
            stories.Select(s => s.StableKey).Should().OnlyHaveUniqueItems(path);
        }

        var skole = specs.Single(s => s.Key.EndsWith("SkoleService/specs/001-skoleservice/spec.md", StringComparison.Ordinal)).Value;
        var past = skole.Model.AcceptanceScenarios.Single(s => s.Given?.StartsWith("a child past compulsory school age", StringComparison.Ordinal) == true);
        var parent = skole.Model.UserStories.Single(u => u.IsStoryHeading && u.StableKey == past.ParentStoryKey);
        output.WriteLine($"Scenario 'a child past compulsory school age' → {parent.StableKey} '{parent.Title}' P={parent.Priority} scenario {past.NumberInStory}; Independent Test: {parent.IndependentTest?[..Math.Min(80, parent.IndependentTest.Length)]}…");
        // The parent is the story heading written above the scenario in the file (checked against the raw text, not assumed).
        var headingLine = skole.Text.Split('\n').Take(past.LineStart!.Value).Last(l => l.StartsWith("### User Story", StringComparison.Ordinal)).Trim();
        parent.Title.Should().Be(headingLine.TrimStart('#', ' ').Trim());
        parent.StableKey.Should().Be("US6");
        parent.Priority.Should().Be("P2");
        parent.IndependentTest.Should().NotBeNullOrWhiteSpace();
        past.NumberInStory.Should().Be(3);

        var dashboard = specs.Single(s => s.Key.EndsWith("Frontend/specs/001-initial-dashboard/spec.md", StringComparison.Ordinal)).Value.Model;
        dashboard.UserStories.Where(u => u.IsStoryHeading).Should().OnlyContain(u => u.Title.StartsWith("Brukerhistorie", StringComparison.Ordinal) && u.Priority != null && u.IndependentTest != null);
        dashboard.AcceptanceScenarios.Should().NotBeEmpty("Norwegian Gitt/naar/saa scenarios are read").And.OnlyContain(s => s.ParentStoryKey != null);

        var hendelse = specs.Single(s => s.Key.EndsWith("Hendelse/specs/001-hendelsestjenesten/spec.md", StringComparison.Ordinal)).Value.Model;
        hendelse.UserStories.Should().NotContain(u => u.IsStoryHeading, "\"Scenario N:\" headings are not user stories");
        hendelse.AcceptanceScenarios.Should().NotBeEmpty().And.OnlyContain(s => s.ParentStoryKey == null, "scenarios directly under the feature have no story");

        var receiver = specs.Single(s => s.Key.EndsWith("MeldingsutvekslerReceiver/specs/001-meldingsutveksler-receiver/spec.md", StringComparison.Ordinal)).Value.Model;
        receiver.AcceptanceScenarios.Should().OnlyContain(s => s.ParentStoryKey == null || s.ParentStoryKey.StartsWith("US", StringComparison.Ordinal) && !s.ParentStoryKey.Contains("-A"),
            "an in-text [US-A3] is never a parent");
    }
}
