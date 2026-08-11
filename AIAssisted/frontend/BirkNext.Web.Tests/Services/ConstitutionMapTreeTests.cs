using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ConstitutionMapTreeTests
{
    private readonly ConstitutionAnalysisService _svc = new();

    [Fact]
    public void BuildMapTree_SharedChildUnderTwoParents_RendersBothEdges()
    {
        var tree = _svc.BuildMapTree(
        [
            Rule("A", "X"),
            Rule("B", "X"),
            Rule("X"),
        ]);

        tree.Should().ContainSingle(n => n.Rule.RuleId == "A")
            .Which.Children.Should().ContainSingle(n => n.Rule.RuleId == "X");
        tree.Should().ContainSingle(n => n.Rule.RuleId == "B")
            .Which.Children.Should().ContainSingle(n => n.Rule.RuleId == "X");
    }

    [Fact]
    public void BuildMapTree_SimpleCycle_StopsAtCurrentPath()
    {
        var tree = _svc.BuildMapTree(
        [
            Rule("A", "B"),
            Rule("B", "A"),
        ]);

        var a = tree.First(n => n.Rule.RuleId == "A");
        var b = a.Children.Should().ContainSingle(n => n.Rule.RuleId == "B").Subject;

        b.Children.Should().NotContain(n => n.Rule.RuleId == "A");
    }

    [Fact]
    public void BuildMapTree_DeeperSharedBranch_RendersCompleteBranchUnderEachParent()
    {
        var tree = _svc.BuildMapTree(
        [
            Rule("A", "C"),
            Rule("B", "C"),
            Rule("C", "D"),
            Rule("D"),
        ]);

        var cUnderA = tree.Should().ContainSingle(n => n.Rule.RuleId == "A")
            .Which.Children.Should().ContainSingle(n => n.Rule.RuleId == "C").Subject;
        var cUnderB = tree.Should().ContainSingle(n => n.Rule.RuleId == "B")
            .Which.Children.Should().ContainSingle(n => n.Rule.RuleId == "C").Subject;

        cUnderA.Children.Should().ContainSingle(n => n.Rule.RuleId == "D");
        cUnderB.Children.Should().ContainSingle(n => n.Rule.RuleId == "D");
    }

    [Fact]
    public void BuildMapTree_SelfCycle_DoesNotExpandSelfRecursively()
    {
        var tree = _svc.BuildMapTree([Rule("A", "A")]);

        var a = tree.Should().ContainSingle(n => n.Rule.RuleId == "A").Subject;

        a.Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildMapTree_AutorisasjonGovernance_RendersAllResolvedDirectReferences()
    {
        var path = FindSampleConstitution("autorisasjon");
        var doc = _svc.Parse(File.ReadAllText(path));
        var governanceRule = doc.RuleCatalog.Should()
            .ContainSingle(r => r.RuleId == "GOV-001")
            .Subject;
        governanceRule.References.Should().HaveCount(47);

        var resolvedIds = new HashSet<string>(doc.RuleCatalog.SelectMany(r => new[] { r.RuleId }.Concat(r.Aliases)),
            StringComparer.OrdinalIgnoreCase);
        governanceRule.References.Should().OnlyContain(id => resolvedIds.Contains(id));

        var governanceNode = FindNode(_svc.BuildMapTree(doc.RuleCatalog), "GOV-001");

        governanceNode.Should().NotBeNull();
        governanceNode!.Children.Should().HaveCount(47);
    }

    private static ConstitutionRule Rule(string id, params string[] references) => new()
    {
        RuleId = id,
        Title = id,
        RuleType = ConstitutionRuleType.Principle,
        References = references.ToList(),
    };

    private static ConstitutionMapNode? FindNode(IEnumerable<ConstitutionMapNode> nodes, string ruleId)
    {
        foreach (var node in nodes)
        {
            if (node.Rule.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase))
                return node;

            var child = FindNode(node.Children, ruleId);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static string FindSampleConstitution(string sampleName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SampleData", sampleName, "constitution.md");
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find SampleData/{sampleName}/constitution.md from {AppContext.BaseDirectory}");
    }
}
