using BirkNext.Api.Services.SourceAnalysis.Glossary;
using BirkNext.Glossary;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Project glossary: detected from the snapshot's own Markdown, authority from document evidence, proposals kept apart, conflicts surfaced,
/// terminology resolved only within one project's snapshot. Fixtures follow the shape of a real project glossary (sections with
/// Norwegian | Code form | Meaning | Used in | Notes tables, ✅/⚠ markers, pinned names, known inconsistencies).
/// </summary>
public sealed class ProjectGlossaryAnalyzerTests
{
    private const string Glossary = """
        # M2LB domain glossary

        What stays Norwegian in M2LB, and what is English.

        This is the **single list for the whole platform**, required by PS-10 and ADR-029.

        ## 2. Kept domain nouns

        ### 2.1 The child and identity

        | Norwegian | Code form | Meaning | Used in | Notes |
        |---|---|---|---|---|
        | barn | `Barn` | child; the person the platform exists for | all modules | ✅ `BarnId` |
        | fødselsnummer | `Foedselsnummer` | Norwegian national identity number | Person | ✅ BiRK column `Fødselsnummer` is pinned |
        | privat institusjon / statlig institusjon | `PrivatInstitusjon`, `StatligInstitusjon` | institution categories | SkoleService | ✅ |

        ## 3. Pinned names (owned elsewhere, never renamed)

        | Owner | Names | Examples |
        |---|---|---|
        | BiRK | tables, columns, values | `Fødselsnummer`, `BarnPK` |

        ## 7. Known inconsistencies in the code today

        | Issue | Where | Suggested direction |
        |---|---|---|
        | Seed value spelled `Ufodt` | Person seed data | keep until migration |
        """;

    private const string Proposal = """
        # Glossary Pull Request Items (FR-006)

        The item list for the **separate** glossary pull request. `docs/glossary.md` is not edited on this branch.

        | # | Glossary location | Row today | What the row needs to say | Status |
        |---|---|---|---|---|
        | 1 | 2.1 `barn` | lists no CdcReplay | Add CdcReplay to Used in | Draft |
        | 2 | 6 | no `tekst` | Add `tekst` (text) | Architect |
        """;

    private static (string, string)[] M2lb(params (string, string)[] extra) =>
    [
        ("docs/glossary.md", Glossary),
        ("CdcReplay/specs/001-translate-adr-029/contracts/glossary-pull-request-items.md", Proposal),
        ("TjenesteService/specs/005-translate-source-english/spec.md", "Naming rule: `docs/glossary.md`."),
        ("Person/specs/003-translate/spec.md", "Terms follow docs/glossary.md §2."),
        .. extra,
    ];

    private static ProjectGlossarySnapshot Analyze(string project, params (string Path, string Content)[] docs) =>
        ProjectGlossaryAnalyzer.Analyze(Guid.NewGuid(), project, DateTimeOffset.UtcNow, docs);

    [Fact]
    public void CaseA_TheProjectGlossaryIsAuthoritative_FromItsOwnStatementAndReferences_WithSourceSectionsAndRows()
    {
        var g = Analyze("M2LB-language", M2lb());

        var glossary = g.Glossaries.Single(x => x.SourcePath == "docs/glossary.md");
        glossary.Authority.Should().Be(GlossaryAuthority.Authoritative);
        glossary.AuthorityEvidence.Should().Contain(e => e.Contains("single list for the whole platform")).And.Contain(e => e.Contains("Referred to by 3 project documents"));
        glossary.Sections.Should().Contain("2. Kept domain nouns › 2.1 The child and identity").And.Contain("7. Known inconsistencies in the code today");
        g.ProjectName.Should().Be("M2LB-language", "terminology never exists without its project");

        var barn = g.Entries.Single(e => e.Term == "barn");
        barn.Should().Match<ProjectGlossaryEntry>(e => e.PreferredForm == "`Barn`" && e.Meaning!.StartsWith("child") && e.Decision == "✅"
            && e.Category == "2. Kept domain nouns › 2.1 The child and identity" && e.Line == 13);
        barn.Columns.Keys.Should().Equal("Norwegian", "Code form", "Meaning", "Used in", "Notes");
        // Pinned names and known inconsistencies keep the source's own section as category: governed project evidence, not BirkNext categories.
        g.Entries.Single(e => e.Term == "BiRK").Category.Should().Be("3. Pinned names (owned elsewhere, never renamed)");
        g.Entries.Single(e => e.Term.StartsWith("Seed value")).Category.Should().Be("7. Known inconsistencies in the code today");
    }

    [Fact]
    public void CaseC_E_H_TermsResolveFromTheAuthoritativeGlossaryOnly_ProposalsStayProposals_AliasesAreTerminologyOnly()
    {
        var g = Analyze("M2LB-language", M2lb());

        g.Glossaries.Single(x => x.SourcePath.EndsWith("glossary-pull-request-items.md")).Authority.Should().Be(GlossaryAuthority.Proposed);
        ProjectTerminology.Proposals(g).Select(e => e.Decision).Should().Equal("Draft", "Architect");
        ProjectTerminology.Resolve(g, "Foedselsnummer").Should().ContainSingle().Which.MatchedOn.Should().Be("preferred form");
        ProjectTerminology.Resolve(g, "barn").Single().Glossary.SourcePath.Should().Be("docs/glossary.md");
        ProjectTerminology.Resolve(g, "tekst").Should().BeEmpty("a proposed term never resolves as approved terminology");
        var institution = g.Entries.Single(e => e.Term.StartsWith("privat institusjon"));
        ProjectTerminology.Aliases(institution).Should().Contain(["PrivatInstitusjon", "StatligInstitusjon"]);
        ProjectTerminology.Resolve(g, "StatligInstitusjon").Should().ContainSingle("both preferred forms resolve to the one governed row (a terminology relation, not an identity merge)");
    }

    [Fact]
    public void CaseB_D_AnotherProjectWithoutAGlossaryInheritsNothing()
    {
        var m2lb = Analyze("M2LB-language", M2lb());
        var other = Analyze("Project-B", ("README.md", "# Project B\n\nUses the word Barn in a sentence."), ("src/notes.md", "Barn and Foedselsnummer"));

        ProjectTerminology.Resolve(m2lb, "Barn").Should().NotBeEmpty();
        other.Glossaries.Should().BeEmpty();
        other.Entries.Should().BeEmpty();
        ProjectTerminology.Resolve(other, "Barn").Should().BeEmpty("approval is never inherited from another project's glossary");
        ProjectTerminology.Resolve(null, "Barn").Should().BeEmpty();
    }

    [Fact]
    public void CaseF_TwoDocumentsClaimingAuthority_AreAConflict_NoSilentWinner()
    {
        var g = Analyze("M2LB-language", M2lb(("platform/terminology.md", "# Platform terminology\n\nThis is the source of truth for names.\n\n| Term | Meaning |\n|---|---|\n| sak | case |\n")));

        g.Glossaries.Where(x => x.Authority == GlossaryAuthority.Conflict).Select(x => x.SourcePath).Should().BeEquivalentTo(["docs/glossary.md", "platform/terminology.md"]);
        g.HasConflict.Should().BeTrue();
        g.Authoritative.Should().BeEmpty();
        ProjectTerminology.Resolve(g, "Barn").Should().BeEmpty("until the conflict is reviewed no glossary is treated as approved");
    }

    [Fact]
    public void AFeatureAboutTerminology_IsNotAGlossary_AndGenericPathTailsAreNoReferences()
    {
        // Real M2LB-language false positives: a feature titled "... Terminology Fix" (whose research notes say "source of truth" about data
        // files) and its checklists/requirements.md, which every feature folder has.
        var g = Analyze("M2LB-language", M2lb(
            ("HendelseService/specs/002-fix-romning-refdata/spec.md", "# Feature Specification: Reference Data Correction and Terminology Fix\n\nSee checklists/requirements.md."),
            ("HendelseService/specs/002-fix-romning-refdata/research.md", "# Research: Reference Data Correction and Terminology Fix\n\nThe files serve as the development-time source of truth for reference data."),
            ("HendelseService/specs/002-fix-romning-refdata/checklists/requirements.md", "# Specification Quality Checklist: Reference Data Correction and Terminology Fix"),
            ("Person/specs/001/checklists/requirements.md", "# Checklist")));

        g.Glossaries.Select(x => x.SourcePath).Should().NotContain(p => p.Contains("002-fix-romning-refdata"));
        g.Glossaries.Single(x => x.SourcePath == "docs/glossary.md").Authority.Should().Be(GlossaryAuthority.Authoritative, "no false claimant makes it a conflict");
    }

    [Fact]
    public void AFileNameAloneIsNotAuthority()
    {
        var g = Analyze("X", ("docs/glossary.md", "# Glossary\n\n| Term | Meaning |\n|---|---|\n| foo | bar |\n"));
        g.Glossaries.Single().Authority.Should().Be(GlossaryAuthority.Unknown);
        ProjectTerminology.Resolve(g, "foo").Should().BeEmpty();
    }

    [Fact]
    public void CaseG_EachSnapshotKeepsItsOwnGlossary()
    {
        var a = Analyze("M2LB-language", M2lb());
        var b = Analyze("M2LB-language", M2lb() is var docs ? [.. docs.Select(d => d.Item1 == "docs/glossary.md" ? (d.Item1, d.Item2.Replace("| barn | `Barn` |", "| barn | `Child` |")) : d)] : []);

        a.Entries.Single(e => e.Term == "barn").PreferredForm.Should().Be("`Barn`", "the earlier snapshot keeps the glossary it was analyzed with");
        b.Entries.Single(e => e.Term == "barn").PreferredForm.Should().Be("`Child`");
        a.Glossaries.Single(x => x.SourcePath == "docs/glossary.md").ContentHash.Should().NotBe(b.Glossaries.Single(x => x.SourcePath == "docs/glossary.md").ContentHash);
    }
}
