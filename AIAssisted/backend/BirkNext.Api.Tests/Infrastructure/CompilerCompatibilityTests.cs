using System.Text.RegularExpressions;
using FluentAssertions;

namespace BirkNext.Api.Tests.Infrastructure;

/// <summary>
/// CI builds with the .NET 8 SDK while local machines may use a newer one. The .NET 8 compiler parses a pattern that ends in a type or enum
/// member directly followed by "? [" (e.g. <c>kind is A.B or A.C ? [] : …</c>) as the nullable array type <c>A.C?[]</c> and fails with
/// CS1002/CS1513, although newer compilers accept it. Parenthesize the pattern instead: <c>(kind is A.B or A.C) ? [] : …</c>.
/// </summary>
public sealed class CompilerCompatibilityTests
{
    private static readonly Regex AmbiguousPatternConditional =
        new(@"\b(?:is|or|and)\s+(?:not\s+)?[A-Z]\w*(?:\.[A-Z]\w*)+\s*\?\s*\[", RegexOptions.Compiled);

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AIAssisted root not found");
    }

    [Fact]
    public void NoPatternIsFollowedDirectlyByACollectionExpressionConditional()
    {
        var root = SourceRoot();
        var offenders = new[] { "shared", Path.Combine("backend", "BirkNext.Api"), Path.Combine("frontend", "BirkNext.Web") }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f), Line: i + 1, Text: line)))
            .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && AmbiguousPatternConditional.IsMatch(x.Text))
            .Select(x => $"{x.File}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        offenders.Should().BeEmpty("the .NET 8 compiler used by CI cannot parse these; parenthesize the pattern");
    }

    [Theory]
    [InlineData("var c = target is not { } kind || kind is Kind.Default or Kind.Custom ? [] :", true)]
    [InlineData("var c = target is not { } kind || (kind is Kind.Default or Kind.Custom) ? [] :", false)]
    [InlineData("var c = restriction is null ? [] : FacetsOf(restriction);", false)]
    public void TheGuardRecognizesTheAmbiguousForm(string line, bool ambiguous) =>
        AmbiguousPatternConditional.IsMatch(line).Should().Be(ambiguous);
}
