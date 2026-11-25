using System.Text;
using BirkNext.Api.Tests.Services.TestPlanning;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>
/// Audit aid (2026-11-25): writes what the canonical spec parser extracts from a real specification, section by section, to the file named by
/// BIRKNEXT_SPEC_AUDIT_OUT (nothing happens otherwise). Used for the spec-structure compliance report; assertions live in SpecStructureComplianceTests.
/// </summary>
public sealed class SpecStructureInventoryProbe
{
    [Fact]
    public void Writes_the_extracted_inventory_when_requested()
    {
        if (Environment.GetEnvironmentVariable("BIRKNEXT_SPEC_AUDIT_OUT") is not { Length: > 0 } outPath) return;
        var path = Environment.GetEnvironmentVariable("BIRKNEXT_SPEC_AUDIT_SPEC");
        var text = path is { Length: > 0 } ? File.ReadAllText(path) : RequirementSectionsTests.Spec("SkoleService/specs/001-skoleservice/spec.md");
        var m = SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(text), text);
        var sb = new StringBuilder();
        sb.AppendLine($"title={m.Title} feature={m.Feature} status={m.Status} created={m.Created}");
        sb.AppendLine($"stories={m.UserStories.Count} storyHeadings={m.UserStories.Count(u => u.IsStoryHeading)} scenarios={m.AcceptanceScenarios.Count} requirements={m.Requirements.Count} sc={m.SuccessCriteria.Count} edge={m.EdgeCases.Count} clar={m.Clarifications.Count} assumptions={m.Assumptions.Count} entities={m.KeyEntities.Count} security={m.SecurityConsiderations.Count}");
        foreach (var u in m.UserStories)
            sb.AppendLine($"US id={u.AuthoredId} ord={u.Ordinal} heading={u.IsStoryHeading} prio={u.Priority} lines={u.LineStart}-{u.LineEnd} title=«{u.Title}» desc={(u.Description ?? "").Length} why={(u.Why ?? "").Length} indep={(u.IndependentTest ?? "").Length} scen={m.AcceptanceScenarios.Count(s => s.ParentStoryKey == u.StableKey)} reqs=[{string.Join(",", u.LinkedRequirements.Select(r => r.Id))}]");
        foreach (var s in m.AcceptanceScenarios)
            sb.AppendLine($"AS id={s.Id} parent={s.ParentStoryKey} n={s.NumberInStory} line={s.LineStart} G={(s.Given ?? "").Length} W={(s.When ?? "").Length} T={(s.Then ?? "").Length} reqs=[{string.Join(",", s.LinkedRequirements.Select(r => r.Id))}] text=«{Short(s.Title)}» then=«{Short(s.Then)}»");
        foreach (var r in m.Requirements) sb.AppendLine($"FR id={r.Id} cat={r.Category} text=«{Short(r.Text, 80)}»");
        foreach (var c in m.SuccessCriteria) sb.AppendLine($"SC id={c.Id} reqs=[{string.Join(",", c.LinkedRequirements.Select(r => r.Id))}] text=«{Short(c.Text, 90)}»");
        foreach (var e in m.EdgeCases) sb.AppendLine($"EDGE title=«{Short(e.Title, 70)}» reqs=[{string.Join(",", e.RelatedRequirementIds)}] desc={(e.Description ?? "").Length}");
        foreach (var c in m.Clarifications) sb.AppendLine($"CLAR session={c.Session} status={c.Status} note=«{Short(c.StatusNote, 60)}» id={c.Id} affected=[{string.Join(",", c.AffectedElements)}] q=«{Short(c.Question, 80)}» a=«{Short(c.Answer, 80)}»");
        foreach (var a in m.Assumptions) sb.AppendLine($"ASSUME «{Short(a.Title, 70)}» desc={(a.Description ?? "").Length}");
        foreach (var e in m.KeyEntities) sb.AppendLine($"ENTITY «{e.Name}» desc={(e.Description ?? "").Length} attrs={e.Attributes.Count}");
        foreach (var s in m.SecurityConsiderations) sb.AppendLine($"SECURITY «{Short(s.Title, 70)}»");
        foreach (var d in m.Dependencies) sb.AppendLine($"DEP id={d.Id} closed={d.Closed} withdrawn={d.Withdrawn} line={d.Line} reqs=[{string.Join(",", d.RequirementIds)}] owner=«{Short(d.Owner, 40)}» blocks=«{Short(d.Blocks, 40)}» text=«{Short(d.Statement, 70)}»");
        foreach (var o in m.OutOfScope) sb.AppendLine($"SCOPE line={o.Line} «{Short(o.Text, 90)}»");
        File.WriteAllText(outPath, sb.ToString());
    }

    private static string Short(string? s, int n = 60) => s is null ? "" : (s.Replace("\n", " ").Length > n ? s.Replace("\n", " ")[..n] + "…" : s.Replace("\n", " "));
}
