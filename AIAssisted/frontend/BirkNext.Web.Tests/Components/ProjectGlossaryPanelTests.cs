using BirkNext.Glossary;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>The project glossary panel shows ONE snapshot's project glossary: terms, proposals and known inconsistencies apart, authority evidence
/// and provenance visible, neutral when there is none.</summary>
public sealed class ProjectGlossaryPanelTests : BunitContext
{
    private static ProjectGlossarySnapshot M2lb() => new()
    {
        SourceSnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555"), ProjectName = "M2LB-language",
        Glossaries =
        [
            new() { GlossaryId = "docs/glossary.md", SourcePath = "docs/glossary.md", Title = "M2LB domain glossary", Authority = GlossaryAuthority.Authoritative,
                AuthorityEvidence = ["States its authority: \"…single list for the whole platform…\""], EntryCount = 2 },
            new() { GlossaryId = "p.md", SourcePath = "CdcReplay/specs/001/contracts/glossary-pull-request-items.md", Title = "Glossary Pull Request Items", Authority = GlossaryAuthority.Proposed, EntryCount = 1 },
        ],
        Entries =
        [
            new() { EntryId = "a", GlossaryId = "docs/glossary.md", Term = "barn", PreferredForm = "`Barn`", Meaning = "child", Category = "2. Kept domain nouns › 2.1 The child and identity", Decision = "✅", Line = 57,
                Columns = new() { ["Norwegian"] = "barn", ["Code form"] = "`Barn`" } },
            new() { EntryId = "b", GlossaryId = "docs/glossary.md", Term = "Seed value spelled Ufodt", Category = "7. Known inconsistencies in the code today", Line = 300 },
            new() { EntryId = "c", GlossaryId = "p.md", Term = "2.1 `barn`", Category = "A. Rows to change", Decision = "Draft", Line = 14 },
        ],
    };

    [Fact]
    public void Terms_Proposals_AndInconsistencies_AreSeparate_WithProvenance()
    {
        var cut = Render<ProjectGlossaryPanel>(p => p.Add(x => x.Glossary, M2lb()));

        cut.Find("[data-testid=pg-summary]").TextContent.Should().Contain("M2LB-language").And.Contain("docs/glossary.md").And.Contain("1 proposal row in 1 document");
        cut.FindAll("[data-testid=pg-term]").Select(r => r.QuerySelector("th")!.TextContent).Should().Equal("barn");
        cut.Find("[data-testid=pg-term] button").Click();
        cut.Find("[data-testid=pg-term-detail]").TextContent.Should().Contain("Code form").And.Contain("docs/glossary.md").And.Contain("line 57").And.Contain("snapshot 11111111");

        cut.FindAll("[data-testid=pg-tab]").Single(t => t.GetAttribute("data-tab") == "proposals").Click();
        cut.FindAll("[data-testid=pg-term]").Select(r => r.QuerySelector("th")!.TextContent).Should().Equal(["2.1 `barn`"], "proposals are never mixed into the terms");
        cut.FindAll("[data-testid=pg-tab]").Single(t => t.GetAttribute("data-tab") == "inconsistencies").Click();
        cut.FindAll("[data-testid=pg-term]").Single().TextContent.Should().Contain("Ufodt");
        cut.FindAll("[data-testid=pg-tab]").Single(t => t.GetAttribute("data-tab") == "sources").Click();
        cut.FindAll("[data-testid=pg-source]").Select(s => s.GetAttribute("data-authority")).Should().Equal("Authoritative", "Proposed");
    }

    [Fact]
    public void NoGlossary_IsNeutral_AndAConflictIsSaid()
    {
        Render<ProjectGlossaryPanel>(p => p.Add(x => x.Glossary, new ProjectGlossarySnapshot { ProjectName = "Project-B" }))
            .Find("[data-testid=pg-none]").TextContent.Should().Be(ProjectTerminology.NoGlossary);
        var conflict = M2lb() with { Glossaries = [.. M2lb().Glossaries.Select(g => g.Authority == GlossaryAuthority.Authoritative ? g with { Authority = GlossaryAuthority.Conflict } : g)] };
        var cut = Render<ProjectGlossaryPanel>(p => p.Add(x => x.Glossary, conflict));
        cut.Find("[data-testid=pg-conflict]").TextContent.Should().Contain("needs review");
        cut.FindAll("[data-testid=pg-term]").Should().BeEmpty("no glossary is treated as approved until the conflict is reviewed");
    }
    [Fact]
    public void Naming_conventions_pinned_names_and_sources_are_glossary_structure_not_terms()
    {
        var g = M2lb() with
        {
            AnalyzerVersion = 2,
            Entries =
            [
                .. M2lb().Entries.Select(e => e.EntryId == "b" ? e with { Kind = GlossaryRowKind.KnownInconsistency } : e),
                new() { EntryId = "d", GlossaryId = "docs/glossary.md", Term = "Timestamp", Category = "1. How to name something", Line = 34, Kind = GlossaryRowKind.NamingConvention },
                new() { EntryId = "e", GlossaryId = "docs/glossary.md", Term = "BiRK", Category = "3. Pinned names", Line = 183, Kind = GlossaryRowKind.PinnedNames },
            ],
        };
        var cut = Render<ProjectGlossaryPanel>(p => p.Add(x => x.Glossary, g));

        cut.FindAll("[data-testid=pg-term]").Select(r => r.QuerySelector("th")!.TextContent).Should().Equal(["barn"], "a naming convention or pinned-name group is not a term");
        cut.FindAll("[data-testid=pg-tab]").Single(t => t.GetAttribute("data-tab") == "inconsistencies").Click();
        cut.FindAll("[data-testid=pg-term]").Single().TextContent.Should().Contain("Ufodt");
        cut.FindAll("[data-testid=pg-tab]").Single(t => t.GetAttribute("data-tab") == "sources").Click();
        cut.Find("[data-testid=pg-structural]").TextContent.Should().Contain("1 naming convention row").And.Contain("1 pinned-name group row");
        cut.FindAll("[data-testid=pg-legacy-rows]").Should().BeEmpty();
    }
}
