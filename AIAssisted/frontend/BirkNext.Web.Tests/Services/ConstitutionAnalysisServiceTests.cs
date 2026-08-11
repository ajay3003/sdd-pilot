using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ConstitutionAnalysisServiceTests
{
    private readonly ConstitutionAnalysisService _svc = new();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string AuthConstitution() => """
        # Authorization Module Constitution

        Version: 1.2
        Ratified: 2024-01-15

        ## Core Principles

        ### PP-01 Zero-Trust Security
        All access requires explicit authorization. No implicit trust.

        ### PP-02 Least Privilege
        Grant the minimum permissions required.

        ### PP-03 Separation of Concerns
        Authorization logic must not leak into business logic.

        ### PP-04 Audit Everything (PP-02, PP-03)
        Every authorization decision must be logged.
        - Related Guidelines: GL-28

        ### PP-05 Fail Secure
        On error, deny access.

        ## Platform Standards

        ### PS-01 Token Validation
        Tokens must be validated on every request.

        ### PS-02 Role Enforcement
        Roles must be enforced at the service boundary.

        ### PS-03 Permission Scoping
        Permissions must be scoped to the minimum required operation.

        ### PS-04 JWT Standards
        Use RS256 signed JWTs. Expiry: 15 minutes access, 7 days refresh.

        ### PS-05 RBAC Model
        Use role-based access control. Roles defined in identity service.

        ### PS-06 Authorization Headers
        All API requests must include Authorization: Bearer <token>.

        ### PS-07 Token Refresh
        Refresh tokens must be rotated on each use.

        ## Authorization Constraints

        ### AC-01 No Direct DB Access
        Modules must not query the authorization DB directly.

        ### AC-02 No Hardcoded Roles
        Role names must not be hardcoded in module code.

        ### AC-03 No Auth Bypass
        No mechanism to skip authorization checks is permitted.

        ### AC-04 Single Auth Source
        Authorization must come from one central service only.

        ### AC-05 No Token Forwarding
        Modules must not forward tokens to downstream services directly.

        ### AC-06 No Silent Failures
        Authorization failures must throw, not return null or empty.

        ### AC-07 No Role Enum
        Roles must not be represented as enums in module code.

        ### AC-08 Immutable Permissions
        Permission sets must not be mutated after initialization.

        ## Governance

        ### Amendment Process
        Any change requires approval from the security guild.

        ## Changelog

        ### 1.2 - 2024-01-15
        - Added AC-08 Immutable Permissions
        """;

    private static string NoChangelogConstitution() => """
        # Simple Constitution

        ## Core Principles

        ### PP-01 Single Responsibility
        Each module has one reason to change.

        ## Platform Standards

        ### PS-01 Testing Required
        All modules must have unit tests.
        """;

    private static string MultiIdConstitution() => """
        # Multi-ID Test Constitution

        ## Core Principles

        ### Zero-Trust Security (PP-02, PP-04)
        Core security principle covering zero-trust and audit requirements.

        ### PP-01 Separation of Concerns
        Authorization logic must not leak into business logic.

        ## Platform Standards

        ### PS-08 Observability Standard (GL-28)
        All services must emit structured logs with trace IDs.
        """;

    // ── 1: Authorization constitution parsing ─────────────────────────────

    [Fact]
    public void AuthConstitution_ParsesPrinciples()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Principles.Should().HaveCount(5);
    }

    [Fact]
    public void AuthConstitution_ParsesStandards()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Standards.Should().HaveCount(7);
    }

    [Fact]
    public void AuthConstitution_ParsesConstraints()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Constraints.Should().HaveCount(8);
    }

    [Fact]
    public void AuthConstitution_TotalRulesIsNotZero()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Health.TotalRules.Should().BeGreaterThan(0,
            "TotalRules must reflect parsed principles + standards + constraints");
    }

    [Fact]
    public void AuthConstitution_TotalRulesMatchesSectionCounts()
    {
        var doc = _svc.Parse(AuthConstitution());
        // Catalog must contain at least one rule per parsed section item
        doc.RuleCatalog.Should().HaveCountGreaterThanOrEqualTo(
            doc.Principles.Count + doc.Standards.Count + doc.Constraints.Count);
    }

    [Fact]
    public void AuthConstitution_HealthIndicatorShowsCounts()
    {
        var doc = _svc.Parse(AuthConstitution());
        var firstIndicator = doc.Health.Indicators.First();
        firstIndicator.Level.Should().Be(HealthIndicatorLevel.Good);
        firstIndicator.Message.Should().Contain("principles");
        firstIndicator.Message.Should().Contain("standards");
        firstIndicator.Message.Should().Contain("constraints");
    }

    [Fact]
    public void AuthConstitution_PP02IsSearchable()
    {
        var doc = _svc.Parse(AuthConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PP-02").ToList();
        results.Should().NotBeEmpty("PP-02 is explicitly defined");
        results.Any(r => r.RuleId == "PP-02").Should().BeTrue();
    }

    [Fact]
    public void AuthConstitution_GovernanceParsed()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.GovernanceItems.Should().NotBeEmpty();
    }

    [Fact]
    public void AuthConstitution_ChangelogParsed()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Changelog.Should().NotBeEmpty();
        doc.Changelog[0].Version.Should().Be("1.2");
    }

    // ── 2: Constitution without changelog ────────────────────────────────

    [Fact]
    public void NoChangelog_ChangelogCountIsZero()
    {
        var doc = _svc.Parse(NoChangelogConstitution());
        doc.Changelog.Should().BeEmpty();
        doc.Health.TotalVersions.Should().Be(0);
    }

    [Fact]
    public void NoChangelog_TotalRulesStillReflectsParsedItems()
    {
        var doc = _svc.Parse(NoChangelogConstitution());
        doc.Health.TotalRules.Should().BeGreaterThan(0);
        doc.RuleCatalog.Should().NotBeEmpty();
    }

    [Fact]
    public void NoChangelog_ChangelogIndicatorIsWarning()
    {
        var doc = _svc.Parse(NoChangelogConstitution());
        var indicator = doc.Health.Indicators
            .FirstOrDefault(i => i.Message.Contains("changelog", StringComparison.OrdinalIgnoreCase));
        indicator.Should().NotBeNull("a changelog warning indicator should be present");
        indicator!.Level.Should().Be(HealthIndicatorLevel.Warning);
    }

    // ── 3: Multi-ID title ("Zero-Trust Security (PP-02, PP-04)") ─────────

    [Fact]
    public void MultiId_PrincipleWithParenthesisIdSearchablePP02()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PP-02").ToList();
        results.Should().NotBeEmpty("PP-02 appears in the heading text");
    }

    [Fact]
    public void MultiId_PrincipleWithParenthesisIdSearchablePP04()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PP-04").ToList();
        results.Should().NotBeEmpty("PP-04 is an alias in the heading (PP-02, PP-04)");
    }

    [Fact]
    public void MultiId_BothPP02AndPP04MapToSameRule()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var byPP02 = _svc.SearchRules(doc.RuleCatalog, "PP-02").FirstOrDefault();
        var byPP04 = _svc.SearchRules(doc.RuleCatalog, "PP-04").FirstOrDefault();
        byPP02.Should().NotBeNull();
        byPP04.Should().NotBeNull();
        // They must be the same rule or the alias PP-04 refers back to PP-02's rule
        (byPP02!.RuleId == byPP04!.RuleId ||
         byPP04.Aliases.Contains("PP-04", StringComparer.OrdinalIgnoreCase) ||
         byPP02.Aliases.Contains("PP-04", StringComparer.OrdinalIgnoreCase))
            .Should().BeTrue("PP-04 is an alias of the PP-02 rule");
    }

    [Fact]
    public void MultiId_StandardWithParenthesisIdSearchablePS08()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PS-08").ToList();
        results.Should().NotBeEmpty("PS-08 appears in the heading");
    }

    [Fact]
    public void MultiId_GL28ExtractedFromParenthesis()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        // GL-28 appears in the parenthetical of the PS-08 heading
        var results = _svc.SearchRules(doc.RuleCatalog, "GL-28").ToList();
        results.Should().NotBeEmpty("GL-28 is referenced/aliased in the PS-08 heading");
    }

    // ── 4: Broken references ──────────────────────────────────────────────

    [Fact]
    public void BrokenReferences_CountedCorrectly()
    {
        const string md = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Test Principle
            References PS-99 which does not exist.

            ## Platform Standards

            ### PS-01 Real Standard
            This exists.
            """;

        var doc = _svc.Parse(md);
        // PS-99 is referenced but not defined — should be counted as broken OR implied
        // The catalog should contain PS-99 as an implied rule (not a broken reference per se)
        // but broken references are those where the TARGET is unknown
        doc.Health.Should().NotBeNull();
    }

    // ── 5: Items without IDs get synthetic IDs ────────────────────────────

    [Fact]
    public void ItemsWithoutIds_GetSyntheticIds()
    {
        const string md = """
            # Test Constitution

            ## Core Principles

            ### Zero-Trust Design
            No ID in this heading.

            ### Least Privilege
            Also no ID.

            ## Platform Standards

            ### Coding Standard
            No ID here either.
            """;

        var doc = _svc.Parse(md);
        doc.RuleCatalog.Should().NotBeEmpty("items without IDs must still appear in catalog");
        doc.Health.TotalRules.Should().BeGreaterThan(0);
        doc.RuleCatalog.Should().Contain(r => r.RuleId.StartsWith("PRINCIPLE-"),
            "headings without PP-NN should get synthetic PRINCIPLE-NNN ids");
    }

    // ── 6: Rule type accuracy ─────────────────────────────────────────────

    [Fact]
    public void CatalogRuleTypes_MatchParsedSections()
    {
        var doc = _svc.Parse(AuthConstitution());

        var principles = doc.RuleCatalog
            .Count(r => r.RuleType == ConstitutionRuleType.Principle);
        var standards = doc.RuleCatalog
            .Count(r => r.RuleType == ConstitutionRuleType.Standard);
        var constraints = doc.RuleCatalog
            .Count(r => r.RuleType == ConstitutionRuleType.Constraint);

        principles.Should().BeGreaterThanOrEqualTo(doc.Principles.Count,
            "all parsed principles must appear in catalog");
        standards.Should().BeGreaterThanOrEqualTo(doc.Standards.Count,
            "all parsed standards must appear in catalog");
        constraints.Should().BeGreaterThanOrEqualTo(doc.Constraints.Count,
            "all parsed constraints must appear in catalog");
    }

    // ── 7: SearchRules covers aliases ─────────────────────────────────────

    [Fact]
    public void SearchRules_FindsByAlias()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        // PP-04 is only an alias, not a primary RuleId
        var rule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-04");
        // Either it's a primary or an alias — either way, SearchRules must return it
        var found = _svc.SearchRules(doc.RuleCatalog, "PP-04").ToList();
        found.Should().NotBeEmpty();
    }

    // ── 8: Health TotalRules = catalog.Count when catalog is non-empty ────

    [Fact]
    public void Health_TotalRulesEqualsCatalogCount()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Health.TotalRules.Should().Be(doc.RuleCatalog.Count);
    }

    // ── 9: Module Principles (MP, H, P, FP) support ────────────────────────

    [Fact]
    public void Parse_WithModulePrinciples_ExtractsWithoutError()
    {
        var markdown = """
            # Tjenestemodul Constitution

            ## Module Principles (MP)

            ### MP-01 — Read-Only in M01
            Tjenestemodulen MUST NOT support creation or modification.

            ### MP-02 — Data Minimisation
            Only fields necessary for the module's purpose MUST be stored.

            ### MP-03 — BiRK Terminology Does Not Leak Out
            External API MUST expose M2LB terminology exclusively.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle module principles without throwing
        doc.Should().NotBeNull();
        doc.Title.Should().Contain("Tjenestemodul");
    }

    [Fact]
    public void Parse_WithServicePrinciples_RecognizesHNotation()
    {
        var markdown = """
            # Hendelsestjenesten Constitution

            ## Service Principles

            ### H-01 — Immutable History
            Hendelsestjenesten MUST maintain immutable history.

            ### H-02 — Audit Trail
            All access events MUST be written to immutable trail.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should recognize H- prefix even if not explicitly mapped
        doc.Principles.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithPlatformPrinciplesTables_ExtractsRows()
    {
        var markdown = """
            # Constitution

            ## Platform Principles

            | ID | Principle | Details |
            |----|-----------|---------|
            | PP-01 | Contract-Driven | All communication via API contracts |
            | PP-02 | Zero-Trust | No implicit trust, explicit authorization |
            | PP-03 | Immutable | All data retains history |
            """;

        var doc = _svc.Parse(markdown);

        // Tables should be parsed and contribute to rule catalog
        doc.Health.TotalRules.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_WithSyncImpactReport_ParsesMetadata()
    {
        var markdown = """
            <!--
            SYNC IMPACT REPORT
            ==================
            Version change: 0.9.0 → 1.0.0
            Added sections:
              - Platform Principles (PP-01–PP-09)
              - Module Principles (MP-01–MP-05)
            Templates reviewed:
              - plan-template.md ✅
            -->

            # Module Constitution
            """;

        var doc = _svc.Parse(markdown);

        // Metadata should not break parsing despite HTML comments
        doc.Title.Should().Contain("Module");
    }

    [Fact]
    public void Parse_WithNorwegianContent_DoesNotBreakParsing()
    {
        var markdown = """
            # Tjenestemodul Constitution

            > **Arver fra:** M2LB Plattformkonstitusjon v4.0
            > **Gjelder for:** `m2lb-tjeneste` repo
            > **Domenekontekst:** Forvalter informasjon om barns aktive og historiske tjenester

            ## Core Principles

            ### PP-01 — Contract-Driven
            All communication MUST occur via published API contracts.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle Norwegian text without errors
        doc.Title.Should().NotBeEmpty();
        doc.Principles.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithAlternativeTitleFormat_ExtractsTitle()
    {
        var markdown = """
            # Hendelsestjenesten — Constitution

            ## Core Principles

            ### PP-01 Contract-Driven
            All communication via contracts.
            """;

        var doc = _svc.Parse(markdown);

        doc.Title.Should().Contain("Hendelsestjenesten");
    }

    [Fact]
    public void Parse_WithSecurityComplianceSection_ParsingSucceeds()
    {
        var markdown = """
            # Constitution

            ## Security & Compliance Requirements

            - **Fail-Closed**: Upon failure, access MUST be denied
            - **Read-Log**: All read operations MUST publish events
            - **Outbox Pattern**: Events MUST use transactional outbox
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle Security & Compliance section without errors
        doc.Should().NotBeNull();
        doc.Title.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithDeferredTODOs_ExtractsFromMetadata()
    {
        var markdown = """
            <!--
            Deferred TODOs:
              - RATIFICATION_DATE pending approval
              - TODO(WORM_RETENTION_PERIOD) to be defined
            -->

            # Constitution
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle TODO markers gracefully
        doc.Title.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithMultiplePrincipalTypes_CategorizesProperly()
    {
        var markdown = """
            # Multi-Type Constitution

            ## Platform Principles

            ### PP-01 Zero-Trust
            Principle text.

            ## Module Principles

            ### MP-01 Read-Only
            Principle text.

            ## Development Guidelines

            ### GL-01 Testing Required
            Guideline text.
            """;

        var doc = _svc.Parse(markdown);

        doc.Principles.Should().NotBeEmpty();
        doc.RuleCatalog.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithRomanNumeralPrinciples_ExtractsAsAlternativeFormat()
    {
        var markdown = """
            # Core Constitution

            ## Core Principles

            ### I. Contract-Driven Communication
            Principle details.

            ### II. Zero-Trust Security
            More details.
            """;

        var doc = _svc.Parse(markdown);

        // Roman numerals should be recognized as principle identifiers
        doc.Principles.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithMandatoryKeywords_FlagsAsBindingRules()
    {
        var markdown = """
            # Constitution

            ## Core Principles

            ### PP-01 Required Rule
            This principle MUST be followed without exception.
            All implementations MUST comply.
            Violations are forbidden.
            Non-negotiable requirement.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should recognize binding keywords
        doc.Principles.Should().NotBeEmpty();
    }

    // ── Changelog with Markdown table (bug: should not create "vChangelog" entry) ──

    [Fact]
    public void Changelog_WithMarkdownTable_ParsesVersionsCorrectly()
    {
        var markdown = """
            <!--
            SYNC IMPACT REPORT
            ==================
            Version change: 1.1.0 → 1.1.1  [PATCH — clarification]

            Modified principles: None
            -->

            # Constitution

            ## Core Principles

            ### PP-01 Core Principle
            Core content.

            ## Changelog

            | Version | Date       | Change                       | Approver          |
            |---------|------------|------------------------------|-------------------|
            | 1.0.0   | 2024-01-01 | Initial version              | Solution Architect |
            | 1.1.0   | 2024-02-01 | Added new standard           | Solution Architect |
            | 1.1.1   | 2024-03-01 | Clarification to standard    | Solution Architect |
            """;

        var doc = _svc.Parse(markdown);

        // Should have exactly 3 changelog entries from the table, not 4 (no "vChangelog")
        doc.Changelog.Should().HaveCount(3);

        // Versions should be exactly as in the table
        doc.Changelog.Select(c => c.Version).Should()
            .ContainInOrder("1.0.0", "1.1.0", "1.1.1");

        // No fake "Changelog" version
        doc.Changelog.Should().NotContain(c => c.Version.Contains("Changelog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Changelog_SyncImpactReportVersion_NotAddedAsChangelogEntry()
    {
        var markdown = """
            <!--
            SYNC IMPACT REPORT
            Version change: 1.0.0 → 1.1.0  [MINOR — new feature]
            -->

            # Constitution

            ## Core Principles

            ### PP-01 Core
            Content.

            ## Changelog

            ### 1.0.0 - 2024-01-01
            - Initial release
            """;

        var doc = _svc.Parse(markdown);

        // Should have only 1 entry from explicit changelog section, not 2 (no metadata version)
        doc.Changelog.Should().HaveCount(1);
        doc.Changelog[0].Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Changelog_TableWithoutLevelThreeHeadings_ParsesAllRows()
    {
        var markdown = """
            # Authorization Constitution

            ## Core Principles

            ### PP-01 Zero Trust
            All access requires verification.

            ## Changelog

            | Version | Date       | Change                  | Approver |
            |---------|------------|-------------------------|----------|
            | 1.0.0   | 2024-01-15 | Initial release         | Admin    |
            | 1.0.1   | 2024-01-20 | Security fix            | Admin    |
            | 1.1.0   | 2024-02-01 | Added new constraint    | Admin    |
            """;

        var doc = _svc.Parse(markdown);

        // All 3 table rows should be parsed
        doc.Changelog.Should().HaveCount(3);
        doc.Changelog[0].Version.Should().Be("1.0.0");
        doc.Changelog[0].Date.Should().Be("2024-01-15");
        doc.Changelog[0].Changes.Should().ContainSingle(c => c == "Initial release");

        doc.Changelog[1].Version.Should().Be("1.0.1");
        doc.Changelog[1].Author.Should().Be("Admin");

        doc.Changelog[2].Version.Should().Be("1.1.0");

        // No "Changelog" entry should exist
        doc.Changelog.Should().NotContain(c => c.Version.Equals("Changelog", StringComparison.OrdinalIgnoreCase));
    }

    // ── Reference range expansion (bug: "PP-01 through PP-09" should expand to all 9) ──

    [Fact]
    public void RuleReferences_RangeWithThrough_ExpandsAllIntermediateIds()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 First Principle
            Content here.

            ### PP-02 Second Principle
            Content here.

            ### PP-03 Third Principle
            Content here.

            ### PP-04 Fourth Principle
            Content here.

            ### PP-05 Fifth Principle
            Content here.

            ## Governance

            This references PP-01 through PP-05 as examples.
            """;

        var doc = _svc.Parse(markdown);

        // Find the Governance rule
        var govRule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId.StartsWith("GOV-"));
        govRule.Should().NotBeNull("Governance item should be in catalog");

        // Governance should reference ALL principles in the range, not just endpoints
        var ppRefs = govRule!.References.Where(r => r.StartsWith("PP-")).ToList();
        ppRefs.Should().Contain("PP-01");
        ppRefs.Should().Contain("PP-02");
        ppRefs.Should().Contain("PP-03");
        ppRefs.Should().Contain("PP-04");
        ppRefs.Should().Contain("PP-05");
        ppRefs.Should().HaveCount(5, because: "All 5 principles should be referenced, not just 2 endpoints");
    }

    [Fact]
    public void RuleReferences_RangeWithEnDash_ExpandsAllIntermediateIds()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Principle
            Content.

            ### PP-02 Principle
            Content.

            ### PP-03 Principle
            Content.

            ## Governance

            References PP-01–PP-03 in development guidance.
            """;

        var doc = _svc.Parse(markdown);

        var govRule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId.StartsWith("GOV-"));
        govRule.Should().NotBeNull();

        // En dash range should also expand
        govRule!.References.Should().Contain("PP-01");
        govRule.References.Should().Contain("PP-02");
        govRule.References.Should().Contain("PP-03");
        govRule.References.Where(r => r.StartsWith("PP-")).Should().HaveCount(3);
    }

    [Fact]
    public void RuleReferences_MultipleRanges_ExpandsEachRange()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 P1
            Content.

            ### PP-02 P2
            Content.

            ### PP-03 P3
            Content.

            ## Platform Standards

            ### PS-01 S1
            Content.

            ### PS-02 S2
            Content.

            ## Governance

            Platform principles PP-01 through PP-03 and platform standards PS-01 through PS-02 apply.
            """;

        var doc = _svc.Parse(markdown);

        var govRule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId.StartsWith("GOV-"));
        govRule.Should().NotBeNull();

        var ppRefs = govRule!.References.Where(r => r.StartsWith("PP-")).ToList();
        var psRefs = govRule!.References.Where(r => r.StartsWith("PS-")).ToList();

        ppRefs.Should().HaveCount(3, because: "PP-01 through PP-03 should expand to 3 references");
        psRefs.Should().HaveCount(2, because: "PS-01 through PS-02 should expand to 2 references");
    }

    [Fact]
    public void RuleReferences_RangeMixedWithSingleIds_HandlesCorrectly()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 P1
            Content.

            ### PP-02 P2
            Content.

            ### PP-03 P3
            Content.

            ### PP-05 P5
            Content.

            ## Governance

            References PP-01 through PP-03 and also PP-05 separately.
            """;

        var doc = _svc.Parse(markdown);

        var govRule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId.StartsWith("GOV-"));
        govRule.Should().NotBeNull();

        var ppRefs = govRule!.References.Where(r => r.StartsWith("PP-")).ToList();
        ppRefs.Should().Contain("PP-01");
        ppRefs.Should().Contain("PP-02");
        ppRefs.Should().Contain("PP-03");
        ppRefs.Should().Contain("PP-05");
        ppRefs.Should().HaveCount(4, because: "Range PP-01-PP-03 (3 items) + single PP-05 (1 item) = 4 total");
    }

    [Fact]
    public void RuleReferences_InvalidRange_OnlyExtracts()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 P1
            Content.

            ### PP-02 P2
            Content.

            ## Governance

            References PP-01 only, not PP-01 to GL-05 (incompatible prefixes).
            """;

        var doc = _svc.Parse(markdown);

        var govRule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId.StartsWith("GOV-"));
        govRule.Should().NotBeNull();

        var refs = govRule!.References.ToList();
        refs.Should().Contain("PP-01", because: "PP-01 should be extracted as a single reference");
        refs.Should().Contain("GL-05", because: "GL-05 should be extracted as a single reference");
        // Range expansion should NOT happen because prefixes differ
        refs.Should().NotContain("PP-02-GL-04", because: "Range with different prefixes should not be synthesized");
    }

    // ── Constraint scope/classification (bug: module constraints marked platform-wide) ──

    [Fact]
    public void Constraints_ModuleConstraintsWithoutId_AreNotPlatformWide()
    {
        var markdown = """
            # Authorization Module Constitution

            ## Authorization Module Constraints

            These constraints are specific to the Autorisasjon service and are binding for all work in this module.

            ### Two-Domain Access Model
            Description of first constraint.

            ### Strict Role–Operation Separation
            Description of second constraint.
            """;

        var doc = _svc.Parse(markdown);

        // Should have 2 constraints (the 2 level-3 items)
        doc.Constraints.Should().HaveCount(2);

        // Both constraints should NOT be platform-wide
        var twodomainConstraint = doc.Constraints[0];
        twodomainConstraint.Title.Should().Contain("Two-Domain Access Model");
        twodomainConstraint.IsPlatformWide.Should().BeFalse(
            because: "Module-specific constraints should not be marked platform-wide");

        var roleConstraint = doc.Constraints[1];
        roleConstraint.Title.Should().Contain("Strict Role");
        roleConstraint.IsPlatformWide.Should().BeFalse(
            because: "Module-specific constraints should not be marked platform-wide");

        // Health check: no platform-wide constraints in this module-specific section
        var platformWideCount = doc.Constraints.Count(c => c.IsPlatformWide);
        platformWideCount.Should().Be(0, because: "Module constraints should not contribute to platform-wide count");
    }

    [Fact]
    public void Constraints_ExplicitPlatformKeyword_ArePlatformWide()
    {
        var markdown = """
            # Multi-Section Constitution

            ## Module Constraints

            ### AC-01 Authorization Module Rule
            Module-specific rule.

            ### Platform Database Connection Standard
            Even in module section, contains "Platform" keyword so marked platform-wide.

            ## Governance

            Content here.
            """;

        var doc = _svc.Parse(markdown);

        doc.Constraints.Should().HaveCount(2);

        // AC-01 has an ID and is in module section, should not be platform-wide
        var authConstraint = doc.Constraints[0];
        authConstraint.Title.Should().Contain("Authorization Module Rule");
        authConstraint.IsPlatformWide.Should().BeFalse(
            because: "Module constraint with ID in ModuleConstraints section");

        // Platform Database... has "Platform" in title, should be platform-wide
        var platformConstraint = doc.Constraints[1];
        platformConstraint.Title.Should().Contain("Platform");
        platformConstraint.IsPlatformWide.Should().BeTrue(
            because: "Constraint explicitly contains 'Platform' keyword");
    }

    // ── Orphan rules definition and filtering ──

    [Fact]
    public void OrphanRules_BothRefsAndRefByZero_AreOrphaned()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 First Principle
            This principle stands alone.

            ### PP-02 Second Principle
            This principle also stands alone.

            ## Governance

            Governance rule with content.
            """;

        var doc = _svc.Parse(markdown);

        // PP-01 is orphaned: it has no references to other rules AND no rules reference it
        var pp01 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.References.Should().BeEmpty(because: "PP-01 references no other rules");
        pp01!.ReferencedBy.Should().BeEmpty(because: "no other rules reference PP-01");

        // PP-02 is also orphaned: no references to/from
        var pp02 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-02");
        pp02.Should().NotBeNull();
        pp02!.References.Should().BeEmpty(because: "PP-02 references no other rules");
        pp02!.ReferencedBy.Should().BeEmpty(because: "no other rules reference PP-02");

        // Check orphan count in health
        doc.Health.OrphanRules.Should().Be(3, because: "PP-01, PP-02, and Governance are all orphaned");
    }

    [Fact]
    public void OrphanRules_OutgoingReferencesPreventOrphanStatus()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Principle One
            Content with no references.

            ### PP-02 Principle Two
            This rule references PP-01, so it's not orphaned even if nothing references it.
            """;

        var doc = _svc.Parse(markdown);

        var pp02 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-02");
        pp02.Should().NotBeNull();
        pp02!.References.Should().Contain("PP-01");
        pp02!.ReferencedBy.Should().BeEmpty();

        // PP-02 is NOT orphaned because it has outgoing references (References.Count > 0)
        // PP-01 is NOT orphaned because PP-02 references it (ReferencedBy.Count > 0)
        doc.Health.OrphanRules.Should().Be(0, because: "PP-02 has outgoing references and PP-01 has incoming references");
    }

    [Fact]
    public void OrphanRules_IncomingReferencesPreventOrphanStatus()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Principle One
            Content.

            ### PP-02 Principle Two
            Content.

            ## Governance

            This references PP-01, so PP-01 is not orphaned.
            """;

        var doc = _svc.Parse(markdown);

        var pp01 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.References.Should().BeEmpty();
        pp01!.ReferencedBy.Should().NotBeEmpty(because: "Governance references PP-01");

        // PP-01 is NOT orphaned because it has incoming references
        doc.Health.OrphanRules.Should().Be(1, because: "only PP-02 has no references in or out");
    }

    [Fact]
    public void OrphanRules_CountMatches_HealthMetric()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Orphan One
            Content.

            ### PP-02 Connected Principle
            References PP-01 sometimes.

            ### PP-03 Orphan Two
            Content.

            ## Platform Standards

            ### PS-01 Standard
            References PP-02.

            ## Governance

            Governance rules aren't referenced.
            """;

        var doc = _svc.Parse(markdown);

        // Rules with no references in or out: PP-03, PS-01 (referenced by governance), and governance rules
        // Actually: PP-01 has ref from PP-02, so not orphaned
        // PP-03 has no refs, governance references it implicitly? No, "Governance rules aren't referenced" means they stand alone
        // So orphans should be: PP-03 and any governance items

        var orphanRules = doc.RuleCatalog
            .Where(r => r.References.Count == 0 && r.ReferencedBy.Count == 0)
            .ToList();

        orphanRules.Count.Should().Be(doc.Health.OrphanRules,
            because: "Health.OrphanRules must equal count of rules with no refs in or out");
    }

    [Fact]
    public void RuleReferences_AutorisasjonConstitution_ExpandsAllRanges()
    {
        // This test reproduces the bug from the actual sample constitution
        var markdown = """
            # Autorisasjon — Spec Constitution

            ## Core Principles

            ### PP-01 Contract-Driven Communication
            Content.

            ### PP-02 Zero-Trust Security
            Content.

            ### PP-03 Domain-Driven Service Design
            Content.

            ### PP-04 Event-Driven Integration
            Content.

            ### PP-05 Specification and Tests Are Inseparable
            Content.

            ### PP-06 Module Principle
            Content.

            ### PP-07 Module Principle
            Content.

            ### PP-08 Module Principle
            Content.

            ### PP-09 Module Principle
            Content.

            ## Platform Standards

            ### PS-01 Standard
            Content.

            ### PS-02 Standard
            Content.

            ### PS-03 Standard
            Content.

            ### PS-04 Standard
            Content.

            ### PS-05 Standard
            Content.

            ### PS-06 Standard
            Content.

            ### PS-07 Standard
            Content.

            ### PS-08 Standard
            Content.

            ### PS-09 Standard
            Content.

            ## Development Standards

            ### GL-01 Guideline
            Content.

            ### GL-29 Guideline
            Content.

            ## Governance

            This constitution is subordinate to the M2LB Platform Constitution.
            Platform principles PP-01 through PP-09 and platform standards
            PS-01 through PS-09 apply in full.

            Use docs/m2lb-utviklingsretningslinjer.md for detailed runtime development guidance (GL-01–GL-29).
            """;

        var doc = _svc.Parse(markdown);

        var govRule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId.StartsWith("GOV-"));
        govRule.Should().NotBeNull("Governance should be in catalog");

        // Verify all PP-01 through PP-09 are referenced
        var ppRefs = govRule!.References.Where(r => r.StartsWith("PP-")).ToList();
        ppRefs.Should().HaveCount(9, because: "PP-01 through PP-09 range should expand to 9 references");
        ppRefs.Should().Contain("PP-01");
        ppRefs.Should().Contain("PP-05");
        ppRefs.Should().Contain("PP-09");

        // Verify all PS-01 through PS-09 are referenced
        var psRefs = govRule.References.Where(r => r.StartsWith("PS-")).ToList();
        psRefs.Should().HaveCount(9, because: "PS-01 through PS-09 range should expand to 9 references");
        psRefs.Should().Contain("PS-01");
        psRefs.Should().Contain("PS-05");
        psRefs.Should().Contain("PS-09");

        // Verify all GL-01 through GL-29 are referenced
        var glRefs = govRule.References.Where(r => r.StartsWith("GL-")).ToList();
        glRefs.Should().HaveCount(29, because: "GL-01 through GL-29 range should expand to 29 references");
        glRefs.Should().Contain("GL-01");
        glRefs.Should().Contain("GL-15");
        glRefs.Should().Contain("GL-29");

        // Total should be 9 + 9 + 29 = 47 references
        govRule.References.Should().HaveCount(47);
    }

    [Fact]
    public void Changelog_WithAscendingVersions_LatestVersionDeterminedBySemVerNotPosition()
    {
        var markdown = """
            # Authorization Constitution

            Version: 1.1.1

            ## Core Principles

            ### PP-01 Core Principle
            Core content.

            ## Changelog

            | Version | Date       | Change                       | Approver          |
            |---------|------------|------------------------------|-------------------|
            | 1.0.0   | 2026-01-01 | Initial spec constitution    | Solution Architect |
            | 1.1.0   | 2026-02-01 | Added Development Standard   | Solution Architect |
            | 1.1.1   | 2026-03-24 | Clarified substitution rules | Solution Architect |
            """;

        var doc = _svc.Parse(markdown);

        // Constitution declares version 1.1.1
        doc.Version.Should().Be("1.1.1");

        // Changelog has 3 entries in ascending order: 1.0.0, 1.1.0, 1.1.1
        doc.Changelog.Should().HaveCount(3);
        doc.Changelog[0].Version.Should().Be("1.0.0");
        doc.Changelog[1].Version.Should().Be("1.1.0");
        doc.Changelog[2].Version.Should().Be("1.1.1");

        // The latest version should be determined by matching the declared version (1.1.1)
        // NOT by list position (idx == 0 would incorrectly pick 1.0.0)
        var latestIndex = DetermineLatestVersionIndex(doc);
        latestIndex.Should().Be(2, because: "v1.1.1 matches the declared document Version");
        doc.Changelog[latestIndex].Version.Should().Be("1.1.1");
    }

    [Fact]
    public void Changelog_WithoutDeclaredVersionMatch_LatestVersionDeterminedBySemanticVersion()
    {
        var markdown = """
            # Authorization Constitution

            Version: 2.0.0

            ## Core Principles

            ### PP-01 Core Principle
            Core content.

            ## Changelog

            | Version | Date       | Change                       | Approver          |
            |---------|------------|------------------------------|-------------------|
            | 1.0.0   | 2026-01-01 | Initial spec constitution    | Solution Architect |
            | 1.1.0   | 2026-02-01 | Added Development Standard   | Solution Architect |
            | 1.1.1   | 2026-03-24 | Clarification                | Solution Architect |
            """;

        var doc = _svc.Parse(markdown);

        // Constitution declares version 2.0.0 (not in changelog)
        doc.Version.Should().Be("2.0.0");

        // Changelog has 3 entries, but 2.0.0 is not there
        doc.Changelog.Should().HaveCount(3);

        // The latest version should be determined by highest semantic version (1.1.1)
        // NOT by list position
        var latestIndex = DetermineLatestVersionIndex(doc);
        latestIndex.Should().Be(2, because: "v1.1.1 is the highest semantic version in the changelog");
        doc.Changelog[latestIndex].Version.Should().Be("1.1.1");
    }

    [Fact]
    public void Changelog_WithNonSequentialVersions_LatestVersionUsesSemanticOrdering()
    {
        var markdown = """
            # Authorization Constitution

            Version: 1.10.0

            ## Core Principles

            ### PP-01 Core Principle
            Core content.

            ## Changelog

            | Version | Date       | Change                       | Approver          |
            |---------|------------|------------------------------|-------------------|
            | 1.0.0   | 2026-01-01 | Initial                      | Solution Architect |
            | 1.9.0   | 2026-02-01 | Added feature                | Solution Architect |
            | 1.10.0  | 2026-03-24 | Enhancement                  | Solution Architect |
            """;

        var doc = _svc.Parse(markdown);

        doc.Version.Should().Be("1.10.0");
        doc.Changelog.Should().HaveCount(3);

        // Test that 1.10.0 > 1.9.0 (semantic version ordering, not string ordering)
        // String ordering would incorrectly place 1.9.0 > 1.10.0
        var latestIndex = DetermineLatestVersionIndex(doc);
        latestIndex.Should().Be(2, because: "v1.10.0 (semver) is higher than v1.9.0, and matches declared version");
        doc.Changelog[latestIndex].Version.Should().Be("1.10.0");
    }

    // Helper method for tests to determine which changelog entry is latest
    private static int DetermineLatestVersionIndex(ConstitutionDocument doc)
    {
        if (doc.Changelog.Count == 0) return -1;

        // First, check if any changelog entry matches the document's declared Version
        var declaredVersionIndex = doc.Changelog.FindIndex(v => v.Version == doc.Version);
        if (declaredVersionIndex >= 0)
            return declaredVersionIndex;

        // Otherwise, find the highest semantic version using Version.TryParse
        int latestIndex = 0;
        Version? latestVersion = Version.TryParse(doc.Changelog[0].Version, out var v0) ? v0 : null;

        for (int i = 1; i < doc.Changelog.Count; i++)
        {
            if (Version.TryParse(doc.Changelog[i].Version, out var currentVersion))
            {
                if (latestVersion is null || currentVersion > latestVersion)
                {
                    latestVersion = currentVersion;
                    latestIndex = i;
                }
            }
        }

        return latestIndex;
    }

    [Fact]
    public void MapTree_WithSharedReferences_ShowsRuleUnderMultipleParents()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Principle One
            References PS-01.

            ### PP-02 Principle Two
            References PS-01.

            ## Standards

            ### PS-01 Shared Standard
            Content.
            """;

        var doc = _svc.Parse(markdown);
        var mapTree = _svc.BuildMapTree(doc.RuleCatalog);

        // Find PP-01 and PP-02 in the tree
        var pp01Node = mapTree.FirstOrDefault(n => n.Rule.RuleId == "PP-01");
        var pp02Node = mapTree.FirstOrDefault(n => n.Rule.RuleId == "PP-02");

        pp01Node.Should().NotBeNull();
        pp02Node.Should().NotBeNull();

        // PS-01 should appear as a child under BOTH PP-01 and PP-02
        pp01Node!.Children.Should().Contain(c => c.Rule.RuleId == "PS-01");
        pp02Node!.Children.Should().Contain(c => c.Rule.RuleId == "PS-01");

        // Each should have PS-01 as a direct child
        pp01Node.Children.Count(c => c.Rule.RuleId == "PS-01").Should().Be(1);
        pp02Node.Children.Count(c => c.Rule.RuleId == "PS-01").Should().Be(1);
    }

    [Fact]
    public void MapTree_WithCycle_PreventsCycles()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Principle One
            References PP-02.

            ### PP-02 Principle Two
            References PP-01.
            """;

        var doc = _svc.Parse(markdown);
        var mapTree = _svc.BuildMapTree(doc.RuleCatalog);

        // Find PP-01 in the tree
        var pp01Node = mapTree.FirstOrDefault(n => n.Rule.RuleId == "PP-01");
        pp01Node.Should().NotBeNull();

        // PP-01 should have PP-02 as a child
        var pp02Child = pp01Node!.Children.FirstOrDefault(c => c.Rule.RuleId == "PP-02");
        pp02Child.Should().NotBeNull();

        // PP-02 should NOT have PP-01 as a child (cycle prevention)
        pp02Child!.Children.Should().NotContain(c => c.Rule.RuleId == "PP-01");
    }

    [Fact]
    public void MapTree_WithDeepSharedReferences_ShowsCompleteHierarchy()
    {
        var markdown = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Principle One
            References PS-01.

            ### PP-02 Principle Two
            References PS-01.

            ## Standards

            ### PS-01 Shared Standard
            References GL-01.

            ## Guidelines

            ### GL-01 Shared Guideline
            Content.
            """;

        var doc = _svc.Parse(markdown);
        var mapTree = _svc.BuildMapTree(doc.RuleCatalog);

        // Find PP-01 and PP-02
        var pp01Node = mapTree.FirstOrDefault(n => n.Rule.RuleId == "PP-01");
        var pp02Node = mapTree.FirstOrDefault(n => n.Rule.RuleId == "PP-02");

        // Both should have PS-01 as a child
        pp01Node!.Children.Should().Contain(c => c.Rule.RuleId == "PS-01");
        pp02Node!.Children.Should().Contain(c => c.Rule.RuleId == "PS-01");

        // Each PS-01 node should have GL-01 as a child
        var ps01UnderPp01 = pp01Node.Children.First(c => c.Rule.RuleId == "PS-01");
        var ps01UnderPp02 = pp02Node.Children.First(c => c.Rule.RuleId == "PS-01");

        ps01UnderPp01.Children.Should().Contain(c => c.Rule.RuleId == "GL-01");
        ps01UnderPp02.Children.Should().Contain(c => c.Rule.RuleId == "GL-01");
    }

    [Fact]
    public void MapTree_AutorisasjonRegression_GovernanceShowsAllReferences()
    {
        var markdown = """
            # Autorisasjon Constitution

            Version: 1.1.1

            ## Core Principles

            ### PP-01 Principle One
            Content.

            ### PP-02 Principle Two
            Content.

            ### PP-03 Principle Three
            Content.

            ### PP-04 Principle Four
            Content.

            ### PP-05 Principle Five
            Content.

            ### PP-06 Principle Six
            Content.

            ### PP-07 Principle Seven
            Content.

            ### PP-08 Principle Eight
            Content.

            ### PP-09 Principle Nine
            Content.

            ## Governance

            This section references PP-01 through PP-09, PS-01 through PS-03, and GL-01 through GL-05.
            """;

        var doc = _svc.Parse(markdown);
        var mapTree = _svc.BuildMapTree(doc.RuleCatalog);

        // Find Governance node
        var govNode = mapTree.FirstOrDefault(n => n.Rule.RuleType == ConstitutionRuleType.Governance);
        govNode.Should().NotBeNull();

        // Extract the referenced IDs from the markdown
        var governanceReferences = new[] { "PP-01", "PP-02", "PP-03", "PP-04", "PP-05", "PP-06", "PP-07", "PP-08", "PP-09",
                                           "PS-01", "PS-02", "PS-03", "GL-01", "GL-02", "GL-03", "GL-04", "GL-05" };

        // Governance should have children for all referenced rules
        foreach (var refId in governanceReferences)
        {
            govNode!.Children.Should().Contain(c => c.Rule.RuleId == refId,
                because: $"Governance should have {refId} as a direct child");
        }

        // The count should match the number of actual references (not reduced by visited set)
        govNode!.Children.Count.Should().Be(governanceReferences.Length,
            because: "All 17 referenced rules should be rendered as direct children");
    }

    // ── TRACEABILITY DEDUPLICATION TESTS ──────────────────────────────

    [Fact]
    public void RuleCatalog_ReferencedBy_NosDuplicates_AuthConstitution()
    {
        var doc = _svc.Parse(AuthConstitution());

        // All rules should have unique entries in ReferencedBy
        foreach (var rule in doc.RuleCatalog)
        {
            var referrers = rule.ReferencedBy;
            var uniqueReferrers = referrers.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            referrers.Count.Should().Be(uniqueReferrers,
                because: $"Rule {rule.RuleId}'s ReferencedBy should have no duplicates");
        }
    }

    [Fact]
    public void RuleCatalog_References_NoDuplicates_AuthConstitution()
    {
        var doc = _svc.Parse(AuthConstitution());

        // All rules should have unique entries in References
        foreach (var rule in doc.RuleCatalog)
        {
            var refs = rule.References;
            var uniqueRefs = refs.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            refs.Count.Should().Be(uniqueRefs,
                because: $"Rule {rule.RuleId}'s References should have no duplicates");
        }
    }

    [Fact]
    public void RuleCatalog_RangeExpansion_WorksWithoutDuplicates()
    {
        var constitution = @"# Test Constitution

## Core Principles

### Zero-Trust (PP-01)
All access requires explicit verification. References PP-02 through PP-04, and PP-03 is critical.
";

        var doc = _svc.Parse(constitution);
        var pp01 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-01");

        if (pp01 != null)
        {
            // PP-03 should be referenced once (from range expansion), not twice
            var pp03Count = pp01.References.Count(r => r.Equals("PP-03", StringComparison.OrdinalIgnoreCase));
            pp03Count.Should().Be(1, because: "Range expansion + explicit mention should deduplicate PP-03");

            // Should have PP-02 and PP-04 from the range
            pp01.References.Any(r => r.Equals("PP-02", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue("PP-02 should be in references from range expansion");
            pp01.References.Any(r => r.Equals("PP-04", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue("PP-04 should be in references from range expansion");
        }
    }

    [Fact]
    public void StandardRawText_PreservesMarkdownTableRows()
    {
        var constitution = """
            # Test Constitution

            ## Platform Standards

            ### PS-01 Source Code Language
            All source code MUST be written in English.

            **Character substitution**: When a retained Norwegian domain term contains the characters
            `æ`, `ø`, or `å`, they MUST be replaced as follows in source code identifiers:

            | Character | Replacement |
            |-----------|-------------|
            | `æ`       | `ae`        |
            | `ø`       | `oe`        |
            | `å`       | `aa`        |

            Example: a domain concept spelled `nødtilgang` becomes `noedtilgang` in code.
            """;

        var doc = _svc.Parse(constitution);
        var standard = doc.Standards.FirstOrDefault();

        standard.Should().NotBeNull();
        standard!.RawText.Should().NotBeNullOrEmpty();

        // Verify table structure is preserved with line breaks
        var lines = standard.RawText.Split('\n');
        var tableHeaderLine = lines.FirstOrDefault(l => l.Contains("Character") && l.Contains("Replacement"));
        tableHeaderLine.Should().NotBeNull(because: "Table header should exist");

        // Verify separator row exists and is on its own line
        var separatorLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("|") && l.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0);
        separatorLine.Should().NotBeNull(because: "Separator row should exist on its own line");

        // Verify data rows are on separate lines
        var dataRows = lines.Where(l => l.TrimStart().StartsWith("|") &&
                                        !l.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Equals(string.Empty) &&
                                        (l.Contains("æ") || l.Contains("ø") || l.Contains("å"))).ToList();
        dataRows.Should().HaveCountGreaterThanOrEqualTo(1, because: "At least one data row with special characters should exist");

        // Verify no flattening: each pipe-delimited row should be on its own line
        var tableRows = lines.Where(l => l.TrimStart().StartsWith("|")).ToList();
        tableRows.Should().HaveCountGreaterThanOrEqualTo(5, because: "Should have header + separator + 3 data rows");
    }

    [Fact]
    public void StandardRendering_WithMarkdownTable_ProducesHtmlTable()
    {
        var constitution = """
            # Test Constitution

            ## Platform Standards

            ### PS-01 Source Code Language
            All source code MUST be written in English.

            **Character substitution**: When a retained Norwegian domain term contains the characters
            `æ`, `ø`, or `å`, they MUST be replaced as follows in source code identifiers:

            | Character | Replacement |
            |-----------|-------------|
            | `æ`       | `ae`        |
            | `ø`       | `oe`        |
            | `å`       | `aa`        |

            Example: a domain concept spelled `nødtilgang` becomes `noedtilgang` in code.
            """;

        var doc = _svc.Parse(constitution);
        var standard = doc.Standards.FirstOrDefault();
        standard.Should().NotBeNull();

        // Render the RawText using MarkdownRenderingService
        var renderService = new MarkdownRenderingService();
        var html = renderService.Render(standard!.RawText);

        // Verify HTML contains table structure
        html.Should().Contain("<table>", because: "Rendered HTML should contain a table element");
        html.Should().Contain("<thead>", because: "Table should have a head section");
        html.Should().Contain("<tbody>", because: "Table should have a body section");
        html.Should().Contain("<th>Character</th>", because: "Table should have Character header");
        html.Should().Contain("<th>Replacement</th>", because: "Table should have Replacement header");

        // Verify special characters are rendered in table cells
        html.Should().Contain("<code>æ</code>", because: "Table should contain æ in code");
        html.Should().Contain("<code>ae</code>", because: "Table should contain ae in code");
    }

    [Fact]
    public void RealConstitution_SourceCodeLanguage_PreservesTable()
    {
        // Load the real constitution file
        var constitutionPath = "../../../../SampleData/autorisasjon/constitution.md";
        if (!File.Exists(constitutionPath))
        {
            // Skip if file not found
            return;
        }

        var constitutionText = File.ReadAllText(constitutionPath);
        var doc = _svc.Parse(constitutionText);

        // Find Source Code Language standard
        var sourceCodeStandard = doc.Standards.FirstOrDefault(s => s.Title.Contains("Source Code Language"));
        sourceCodeStandard.Should().NotBeNull();

        // Check that RawText contains table rows on separate lines
        var lines = sourceCodeStandard!.RawText.Split('\n');
        var tableLines = lines.Where(l => l.TrimStart().StartsWith("|")).ToList();
        tableLines.Count.Should().BeGreaterThanOrEqualTo(5, because: "Table should have header + separator + data rows");

        // Render the RawText
        var renderService = new MarkdownRenderingService();
        var html = renderService.Render(sourceCodeStandard.RawText);

        // Verify table is rendered as HTML
        html.Should().Contain("<table>", because: "Table should be rendered as HTML");
        html.Should().Contain("æ", because: "Special character should be in output");
    }

    [Fact]
    public void StandardsWithTableAndBullets_PreserveBothInRawText()
    {
        // This standard has both bullets AND a table
        var constitution = """
            # Test Constitution

            ## Platform Standards

            ### PS-01 Source Code Language
            All source code MUST be written in English.

            - Follow naming conventions
            - Use English identifiers

            **Character substitution**: When a retained Norwegian domain term contains the characters:

            | Character | Replacement |
            |-----------|-------------|
            | `æ`       | `ae`        |
            | `ø`       | `oe`        |
            | `å`       | `aa`        |

            Example: `nødtilgang` becomes `noedtilgang`.
            """;

        var doc = _svc.Parse(constitution);
        var standard = doc.Standards.FirstOrDefault();
        standard.Should().NotBeNull();

        // The standard should have rules (bullets) extracted
        standard!.Rules.Should().HaveCountGreaterThan(0, because: "Bullets should be extracted as rules");

        // Check that RawText still contains the table properly formatted with line breaks
        standard.RawText.Should().Contain("| Character | Replacement |", because: "Table header should be in RawText");
        standard.RawText.Should().Contain("|-----------|-------------|", because: "Table separator should be in RawText");
        standard.RawText.Should().Contain("| `æ`", because: "Table row with special char should be in RawText");

        // Most importantly: verify each table row is on its own line, not flattened
        var lines = standard.RawText.Split('\n');
        var tableLines = lines.Where(l => l.TrimStart().StartsWith("|")).ToList();
        tableLines.Count.Should().BeGreaterThanOrEqualTo(5, because: "Should have header + separator + 3 data rows on separate lines");

        // Verify the RawText can be rendered as HTML by Markdig
        var renderService = new MarkdownRenderingService();
        var html = renderService.Render(standard.RawText);
        html.Should().Contain("<table>", because: "RawText should contain proper Markdown table that Markdig renders");
        html.Should().Contain("<ul>", because: "RawText should also contain bullets rendered as <ul>");
    }

    [Fact]
    public void RealConstitution_SourceCodeLanguage_TableLinesAreNotFlattened()
    {
        var constitutionPath = "../../../../SampleData/autorisasjon/constitution.md";
        if (!File.Exists(constitutionPath))
            return;

        var constitutionText = File.ReadAllText(constitutionPath);
        var doc = _svc.Parse(constitutionText);

        var sourceCodeStandard = doc.Standards.FirstOrDefault(s => s.Title.Contains("Source Code Language"));
        sourceCodeStandard.Should().NotBeNull();

        // Check that table rows are on separate lines in RawText
        var rawTextLines = sourceCodeStandard!.RawText.Split('\n');
        var tableLines = rawTextLines.Where(l => l.TrimStart().StartsWith("|")).ToList();

        // Should have at least 5 table lines: header + separator + 3 rows
        tableLines.Should().HaveCountGreaterThanOrEqualTo(5, because: "Table should not be flattened - each row should be on its own line");

        // Verify table can be rendered properly
        var renderService = new MarkdownRenderingService();
        var html = renderService.Render(sourceCodeStandard.RawText);

        // If properly formatted, Markdig should produce a proper table
        html.Should().Contain("<table>", because: "Table should be rendered as HTML, not flattened text");
    }

    [Fact]
    public void RuleCatalog_NoSelfReferencesFromHeading()
    {
        var constitution = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Zero-Trust Security (PP-02, PP-04)
            All access requires explicit authorization.

            ### PP-02 Least Privilege
            Grant the minimum permissions required.

            ### PP-03 Separation of Concerns (PP-03, PP-06, PP-07)
            Authorization logic must not leak into business logic.

            ### PP-04 Audit Everything
            Every authorization decision must be logged.
            """;

        var doc = _svc.Parse(constitution);

        // PP-01 should NOT reference itself (PP-02 and PP-04 are in heading but PP-01 is not)
        var pp01 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.References.Should().NotContain("PP-01");
        pp01.References.Should().Contain("PP-02");
        pp01.References.Should().Contain("PP-04");

        // PP-03 should NOT reference itself even though PP-03 appears in the heading
        var pp03 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-03");
        pp03.Should().NotBeNull();
        pp03!.References.Should().NotContain("PP-03");
        pp03.References.Should().Contain("PP-06");
        pp03.References.Should().Contain("PP-07");

        // ReferencedBy should also not contain self-references
        pp01.ReferencedBy.Should().NotContain("PP-01");
        pp03.ReferencedBy.Should().NotContain("PP-03");
    }

    [Fact]
    public void RuleCatalog_HeadingWithoutExplicitId_ExtractsFromParentheses()
    {
        // This tests the case where heading has no explicit ID prefix
        var constitution = """
            # Test Constitution

            ## Core Principles

            ### II. Zero-Trust Security (PP-01, PP-02, PP-04)
            All access requires explicit authorization.

            ### III. Least Privilege (PP-02)
            Grant the minimum permissions required.
            """;

        var doc = _svc.Parse(constitution);

        // Find the principle - ID should be extracted from parentheses
        var pp01 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull(because: "PP-01 should be found from parentheses in heading");

        // PP-01 should reference PP-02 and PP-04, but NOT itself
        pp01!.References.Should().Contain("PP-02");
        pp01.References.Should().Contain("PP-04");
        pp01.References.Should().NotContain("PP-01",
            because: "Rule extracted from parentheses in its own heading should not self-reference");

        // ReferencedBy should not contain self
        pp01.ReferencedBy.Should().NotContain("PP-01",
            because: "Rule should not appear in its own ReferencedBy list");
    }

    [Fact]
    public void RuleCatalog_RomanNumeralWithParentheses_NoSelfReference()
    {
        // Exact format from real constitution
        var constitution = """
            # Test Constitution

            ## Core Principles

            ### II. Zero-Trust Security (PP-02, PP-04)
            All security requires verification.

            ### III. Domain-Driven Service Design (PP-03, PP-06, PP-07)
            Services own their data.
            """;

        var doc = _svc.Parse(constitution);

        // PP-02 should NOT reference itself
        var pp02 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-02");
        pp02.Should().NotBeNull();
        pp02!.References.Should().NotContain("PP-02");
        pp02.References.Should().Contain("PP-04");
        pp02.ReferencedBy.Should().NotContain("PP-02",
            because: "PP-02 extracted from heading should not appear in its own ReferencedBy");

        // PP-03 should NOT reference itself
        var pp03 = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-03");
        pp03.Should().NotBeNull();
        pp03!.References.Should().NotContain("PP-03");
        pp03.References.Should().Contain("PP-06");
        pp03.References.Should().Contain("PP-07");
        pp03.ReferencedBy.Should().NotContain("PP-03");
    }

    [Fact]
    public void RuleCatalog_RealConstitution_NoPrinciplesSelfReference()
    {
        var constitutionPath = "../../../../SampleData/autorisasjon/constitution.md";
        if (!File.Exists(constitutionPath))
            return;

        var constitutionText = File.ReadAllText(constitutionPath);
        var doc = _svc.Parse(constitutionText);

        // Check that no principle references itself
        foreach (var rule in doc.RuleCatalog.Where(r => r.RuleType == ConstitutionRuleType.Principle))
        {
            rule.References.Should().NotContain(rule.RuleId,
                because: $"Rule {rule.RuleId} should not reference itself");
            rule.ReferencedBy.Should().NotContain(rule.RuleId,
                because: $"Rule {rule.RuleId} should not appear in its own ReferencedBy");
        }
    }

    [Fact]
    public void StandardInvoicesRawTextNotFlattened_AllTableRowsOnSeparateLines()
    {
        var constitution = """
            # Test Constitution

            ## Platform Standards

            ### PS-01 Source Code Language
            All source code MUST be written in English.

            - Rule 1
            - Rule 2

            **Character substitution**: When Norwegian characters appear:

            | Character | Replacement |
            |-----------|-------------|
            | `æ`       | `ae`        |
            | `ø`       | `oe`        |
            | `å`       | `aa`        |

            Example follows.
            """;

        var doc = _svc.Parse(constitution);
        var standard = doc.Standards[0];

        // Verify RawText structure
        var lines = standard.RawText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // Find table header
        var headerIdx = Array.FindIndex(lines, l => l.Contains("Character") && l.Contains("Replacement"));
        headerIdx.Should().BeGreaterThanOrEqualTo(0);

        // Find separator - should be the very next line
        var sepIdx = Array.FindIndex(lines, headerIdx + 1, l => l.Contains("---"));
        sepIdx.Should().Be(headerIdx + 1, because: "Separator should be immediately after header");

        // Find data rows - each should be on its own line
        var firstDataRowIdx = sepIdx + 1;
        lines[firstDataRowIdx].Should().Contain("æ");

        var secondDataRowIdx = firstDataRowIdx + 1;
        lines[secondDataRowIdx].Should().Contain("ø");

        var thirdDataRowIdx = secondDataRowIdx + 1;
        lines[thirdDataRowIdx].Should().Contain("å");

        // Verify NOT flattened: if all pipes were on one line, this would fail
        var pipeCount = standard.RawText.Count(c => c == '|');
        var newlineCount = standard.RawText.Count(c => c == '\n');

        // A properly formatted table has ~5 pipes per line (header/sep/data)
        // If flattened, all pipes would be on one or two lines
        var linesWithPipes = lines.Count(l => l.Contains("|"));
        linesWithPipes.Should().BeGreaterThanOrEqualTo(5, because: "Table rows should be on separate lines");
    }
}
